using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;

public class MethodProcessor
{
    public ModuleWeaver ModuleWeaver;
    public TypeSystem TypeSystem;
    public MethodDefinition Method;
    MethodBody _body;
    VariableDefinition _stopwatchVar;
    FieldDefinition _stopwatchField;
    TypeDefinition _asyncStateMachineType;

    public void Process()
    {
        try
        {
            if (Method.IsAsync())
            {
                InnerProcessAsync();
            }
            else
            {
                InnerProcess();
            }
        }
        catch (Exception exception)
        {
            throw new WeavingException(string.Format("An error occurred processing '{0}'. Error: {1}", Method.FullName, exception.Message));
        }
    }

    void InnerProcess()
    {
        _body = Method.Body;
        _body.SimplifyMacros();

        InjectStopwatch();
        HandleReturns();

        _body.InitLocals = true;
        _body.OptimizeMacros();
    }

    void InnerProcessAsync()
    {
        // Find state machine type
        var asyncAttribute = Method.GetAsyncStateMachineAttribute();
        _asyncStateMachineType = (from ctor in asyncAttribute.ConstructorArguments
                                  select (TypeDefinition)ctor.Value).First();

        // Find the MoveNext method
        var moveNextMethod = (from method in _asyncStateMachineType.Methods
                              where string.Equals(method.Name, "MoveNext")
                              select method).First();

        _body = moveNextMethod.Body;
        _body.SimplifyMacros();

        // Find the real start of the "method"
        var startInstructionIndex = FindMethodStartAsync(_body.Instructions);

        // Inject the stopwatch
        InjectStopwatchAsync(_asyncStateMachineType, _body.Instructions, startInstructionIndex);

        // Handle the returns in async mode
        HandleReturnsAsync();

        _body.InitLocals = true;
        _body.OptimizeMacros();
    }

    void HandleReturns()
    {
        var instructions = _body.Instructions;

        var returnPoints = instructions.Where(x => x.OpCode == OpCodes.Ret).ToList();

        foreach (var returnPoint in returnPoints)
        {
            FixReturn(instructions, returnPoint);
        }

        var last = instructions.Last();
        if (last.OpCode == OpCodes.Rethrow || last.OpCode == OpCodes.Throw)
        {
            FixReturn(instructions, last);
        }
    }

    void HandleReturnsAsync()
    {
        var instructions = _body.Instructions;

        // There are 3 possible return points:
        // 
        // 1) async code:
        //      awaiter.GetResult();
        //      awaiter = new TaskAwaiter();
        //
        // 2) exception handling
        //      L_00d5: ldloc.1 
        //      L_00d6: call instance void [mscorlib]System.Runtime.CompilerServices.AsyncTaskMethodBuilder::SetException(class [mscorlib]System.Exception)
        //
        // 3) all other returns
        //
        // We can do this smart by searching for all leave and leave_S op codes and check if they point to the last
        // instruction of the method. This equals a "return" call.

        // 1) async code

        //var getResultInstruction = (from instruction in instructions
        //                            where instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference
        //                                  && string.Equals(((MethodReference)instruction.Operand).Name, "GetResult")
        //                            select instruction).First();

        //var getResultIndex = instructions.IndexOf(getResultInstruction);

        //var nextLeaveStatement = 0;
        //for (var i = getResultIndex; i < instructions.Count; i++)
        //{
        //    var instruction = instructions[i];
        //    if (instruction.IsLeaveInstruction())
        //    {
        //        nextLeaveStatement = i;
        //        break;
        //    }
        //}

        //if (instructions[nextLeaveStatement - 1].OpCode == OpCodes.Nop)
        //{
        //    nextLeaveStatement--;
        //}

        //var finalInstruction = instructions[nextLeaveStatement];

        //FixReturn(instructions, finalInstruction);


        // 2) Exception handling 

        //var setExceptionMethod = (from instruction in instructions
        //                          where instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference
        //                                && string.Equals(((MethodReference)instruction.Operand).Name, "SetException")
        //                          select instruction).First();

        //var setExceptionMethodIndex = instructions.IndexOf(setExceptionMethod);

        //FixReturn(instructions, instructions[setExceptionMethodIndex + 1]);

        // 3) All leave statements to the last label

        //var lastReturn = (from instruction in instructions
        //                  where instruction.OpCode == OpCodes.Ret
        //                  select instruction).Last();

        var possibleReturnStatements = new List<Instruction>();
        //possibleReturnStatements.Add(lastReturn);

        //var lineBeforeLastReturn = instructions[instructions.IndexOf(lastReturn) - 1];
        //if (lineBeforeLastReturn.OpCode == OpCodes.Nop)
        //{
        //    possibleReturnStatements.Add(lineBeforeLastReturn);
        //}

        for (var i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i].IsLeaveInstruction())
            {
                possibleReturnStatements.Add(instructions[i + 1]);
                break;
            }
        }

        for (int i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.IsLeaveInstruction())
            {
                if (possibleReturnStatements.Any(x => ReferenceEquals(instruction.Operand, x)))
                {
                    // This is a return statement
                    var instructionsAdded = FixReturn(instructions, instruction);
                    i += instructionsAdded;
                }
            }
        }
    }

    int FixReturn(Collection<Instruction> instructions, Instruction returnPoint)
    {
        var opCode = returnPoint.OpCode;
        var operand = returnPoint.Operand as Instruction;

        returnPoint.OpCode = OpCodes.Nop;
        returnPoint.Operand = null;

        var instructionsAdded = 0;
        var indexOf = instructions.IndexOf(returnPoint);
        foreach (var instruction in GetWriteTimeIL())
        {
            indexOf++;
            instructions.Insert(indexOf, instruction);
            instructionsAdded++;
        }

        indexOf++;

        if ((opCode == OpCodes.Leave) || (opCode == OpCodes.Leave_S))
        {
            instructions.Insert(indexOf, Instruction.Create(opCode, operand));
            instructionsAdded++;
        }
        else
        {
            instructions.Insert(indexOf, Instruction.Create(opCode));
            instructionsAdded++;
        }

        return instructionsAdded;
    }

    private IEnumerable<Instruction> GetWriteTimeIL()
    {
        foreach (var instruction in GetLoadStopwatchInstruction())
        {
            yield return instruction;
        }

        yield return Instruction.Create(OpCodes.Call, ModuleWeaver.StopMethod);
        if (ModuleWeaver.LogMethod == null)
        {
            yield return Instruction.Create(OpCodes.Ldstr, Method.MethodName());

            foreach (var instruction in GetLoadStopwatchInstruction())
            {
                yield return instruction;
            }

            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.ElapsedMilliseconds);
            yield return Instruction.Create(OpCodes.Box, TypeSystem.Int64);
            yield return Instruction.Create(OpCodes.Ldstr, "ms");
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.ConcatMethod);
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.DebugWriteLineMethod);
        }
        else
        {
            yield return Instruction.Create(OpCodes.Ldtoken, Method);
            yield return Instruction.Create(OpCodes.Ldtoken, Method.DeclaringType);
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.GetMethodFromHandle);

            foreach (var instruction in GetLoadStopwatchInstruction())
            {
                yield return instruction;
            }

            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.ElapsedMilliseconds);
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.LogMethod);
        }
    }

    Instruction[] GetLoadStopwatchInstruction()
    {
        if (_stopwatchVar != null)
        {
            return new[]
            {
                Instruction.Create(OpCodes.Ldloc, _stopwatchVar)
            };
        }

        if (_stopwatchField != null)
        {
            return new[]
            {
                Instruction.Create(OpCodes.Ldarg_0),
                Instruction.Create(OpCodes.Ldfld, _stopwatchField)
            };
        }

        return new Instruction[] { };
    }

    void InjectStopwatch()
    {
        // inject as variable
        _stopwatchVar = new VariableDefinition("methodTimerStopwatch", ModuleWeaver.StopwatchType);
        _body.Variables.Add(_stopwatchVar);

        _body.Instructions.Insert(0, new List<Instruction>(new[] {
            Instruction.Create(OpCodes.Call, ModuleWeaver.StartNewMethod),
            Instruction.Create(OpCodes.Stloc, _stopwatchVar)
        }));
    }

    void InjectStopwatchAsync(TypeDefinition typeDefinition, Collection<Instruction> instructions, int instructionIndex)
    {
        // inject as field
        _stopwatchField = new FieldDefinition("methodTimerStopwatch", new FieldAttributes(), ModuleWeaver.StopwatchType);
        typeDefinition.Fields.Add(_stopwatchField);

        instructions.Insert(instructionIndex, new List<Instruction>(new[] {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, ModuleWeaver.StartNewMethod),
            Instruction.Create(OpCodes.Stfld, _stopwatchField)
        }));
    }

    static int FindMethodStartAsync(Collection<Instruction> instructions)
    {
        // Inject stopwatch to beginning of "default" label

        var startIndex = -1;
        Instruction startInstruction = null;

        // A) If there is a switch, go to the first:

        // L_000d: switch (L_0034, L_0052, L_0052, L_0039, L_003e, L_0043, L_0048, L_004d)
        // L_0032: br.s L_0052
        // L_0034: br L_0719
        // L_0039: br L_00f3
        // L_003e: br L_01b8
        // L_0043: br L_028f
        // L_0048: br L_0466
        // L_004d: br L_06d3
        // L_0052: br.s L_0054

        if (startInstruction == null)
        {
            var switchInstruction = (from instruction in instructions
                                     where instruction.OpCode == OpCodes.Switch
                                     select instruction).FirstOrDefault();
            if (switchInstruction != null)
            {
                startInstruction = FindLastBrsAsync(instructions, switchInstruction);
            }
        }

        // B) Get the right br.s

        // L_000f: ldc.i4.0         <== ldc.i4.0 which we are looking for
        // L_0010: beq.s L_0016     
        // L_0012: br.s L_0018      <== first br.s that jumpts to address #1
        // L_0014: br.s L_0083
        // L_0016: br.s L_0055      <== br.s that jumps to the actual start of the "method"
        // L_0018: br.s L_001a
        // L_001d: nop 

        if (startInstruction == null)
        {
            var firstBreakInstruction = (from instruction in instructions
                                         where instruction.IsBreakInstruction()
                                         select instruction).FirstOrDefault();
            if (firstBreakInstruction != null)
            {
                startInstruction = FindLastBrsAsync(instructions, firstBreakInstruction);
            }
        }

        // If instruction is nop, increase index
        if (startInstruction != null)
        {
            startIndex = instructions.IndexOf(startInstruction);
            if (startInstruction.OpCode == OpCodes.Nop)
            {
                startIndex++;
            }
        }

        return startIndex;
    }

    static Instruction FindLastBrsAsync(Collection<Instruction> instructions, Instruction startInstruction)
    {
        var wasPreviousBr = false;

        for (int i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.IsBreakInstruction())
            {
                wasPreviousBr = true;
                continue;
            }

            if (!wasPreviousBr)
            {
                continue;
            }

            return instruction;
        }

        return null;
    }
}