using System;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MethodTimer.Fody
{
    public class Configuration
    {
        /// <exception cref="WeavingException">If 'PointcutRegex' could not be parsed as <see cref="Regex" />.</exception>

        public Configuration(XElement element)
        {
            // Regex that matches nothing - http://stackoverflow.com/q/940822/1224069
            PointcutRegex = new Regex("$^", RegexOptions.Compiled);

            // ReSharper disable once UseNullPropagation
            if (null == element)
                return;

            var attribute = element.Attribute("PointcutRegex");

            if (null != attribute)
            {
                try
                {
                    PointcutRegex = new Regex(attribute.Value, RegexOptions.Compiled);
                }
                catch (Exception e)
                {
                    throw new WeavingException(
                        $"Failed to load {attribute.Value} as a {typeof(Regex).FullName} from configuration",
                        e);
                }
            }
        }

        /// <summary>
        /// Regex for matching a Type's <see cref="Type.FullName"/> to indicate 
        /// if it should be timed.
        /// </summary>
        public Regex PointcutRegex { get; }
    }
}
