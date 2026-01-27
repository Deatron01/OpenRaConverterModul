using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenRA.Converter.Core.Models
{
    public class ConverterSettings
    {
        // Ide menti a kész .cs fájlokat (pl. C:\OpenRA\Mods\MyMod\Traits)
        public string CSharpOutputPath { get; set; } = string.Empty;

        // Ez a fő YAML fájl teljes útvonala, amihez hozzáfűzzük az újat (pl. C:\OpenRA\Mods\MyMod\rules.yaml)
        public string YamlRulesFilePath { get; set; } = string.Empty;
    }
}
