using Microsoft.Extensions.Options;
using OpenRA.Converter.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenRA.Converter.Infrastructure.Services
{
    public interface IFileWriterService
    {
        string SaveCSharpFile(string fileName, string content);
        string AppendToYamlRules(string content);
    }

    public class FileWriterService : IFileWriterService
    {
        private readonly ConverterSettings _settings;

        public FileWriterService(IOptions<ConverterSettings> settings)
        {
            _settings = settings.Value;
        }

        public string SaveCSharpFile(string fileName, string content)
        {
            if (string.IsNullOrWhiteSpace(_settings.CSharpOutputPath))
            {
                throw new InvalidOperationException("A CSharpOutputPath nincs beállítva az appsettings.json-ban.");
            }

            // Mappa létrehozása, ha nem létezik
            if (!Directory.Exists(_settings.CSharpOutputPath))
            {
                Directory.CreateDirectory(_settings.CSharpOutputPath);
            }

            var fullPath = Path.Combine(_settings.CSharpOutputPath, fileName);
            File.WriteAllText(fullPath, content);

            return fullPath;
        }

        public string AppendToYamlRules(string content)
        {
            if (string.IsNullOrWhiteSpace(_settings.YamlRulesFilePath))
            {
                throw new InvalidOperationException("A YamlRulesFilePath nincs beállítva az appsettings.json-ban.");
            }

            if (!File.Exists(_settings.YamlRulesFilePath))
            {
                // Ha még nincs ilyen fájl, létrehozzuk
                // De érdemes ellenőrizni a mappát előtte
                var directory = Path.GetDirectoryName(_settings.YamlRulesFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }

            // Hozzáfűzés a fájl végéhez (Append)
            // Beillesztünk egy elválasztó sort is, hogy átlátható legyen
            string contentToAppend = Environment.NewLine + content;

            File.AppendAllText(_settings.YamlRulesFilePath, contentToAppend);

            return _settings.YamlRulesFilePath;
        }
    }
}
