using Microsoft.AspNetCore.Mvc;
using OpenRA.Converter.Core.Interfaces;
using OpenRA.Converter.Infrastructure.Services; // Itt van az IFileWriterService
using System;
using System.Text.Json;

namespace OpenRA.Converter.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SynthesisController : ControllerBase
    {
        private readonly IDecisionTreeService _treeService;
        private readonly ITraitSynthesisService _csharpSynthesisService;
        private readonly IYamlSynthesisService _yamlSynthesisService;
        private readonly ICodeWriter _csharpWriter;
        private readonly IYamlCodeWriter _yamlWriter;
        private readonly IFileWriterService _fileWriter; // Új mező

        public SynthesisController(
            IDecisionTreeService treeService,
            ITraitSynthesisService csharpSynthesisService,
            IYamlSynthesisService yamlSynthesisService,
            ICodeWriter csharpWriter,
            IYamlCodeWriter yamlWriter,
            IFileWriterService fileWriter) // Új paraméter
        {
            _treeService = treeService;
            _csharpSynthesisService = csharpSynthesisService;
            _yamlSynthesisService = yamlSynthesisService;
            _csharpWriter = csharpWriter;
            _yamlWriter = yamlWriter;
            _fileWriter = fileWriter;
        }

        [HttpPost("generate-csharp")]
        public IActionResult GenerateCSharp([FromBody] JsonElement payload, [FromQuery] string traitName = "NewTrait")
        {
            try
            {
                var rootNode = _treeService.ParseTree(payload);
                var validationErrors = _treeService.ValidateTree(rootNode);
                if (validationErrors.Count > 0) return BadRequest(new { Errors = validationErrors });

                var classStructure = _csharpSynthesisService.SynthesizeTrait(rootNode, traitName);
                var code = _csharpWriter.WriteClass(classStructure);

                // --- Fájlba mentés ---
                string fileName = $"{traitName}.cs";
                string savedPath = _fileWriter.SaveCSharpFile(fileName, code);

                return Ok(new
                {
                    FileName = fileName,
                    SavedPath = savedPath, // Visszaadjuk, hova mentettük
                    Code = code,
                    DetectedDependencies = classStructure.RequiredYamlInherits
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        [HttpPost("generate-yaml")]
        public IActionResult GenerateYaml([FromBody] JsonElement payload, [FromQuery] string actorName = "MyActor", [FromQuery] string traitName = "NewTrait")
        {
            try
            {
                var rootNode = _treeService.ParseTree(payload);
                var validationErrors = _treeService.ValidateTree(rootNode);
                if (validationErrors.Count > 0) return BadRequest(new { Errors = validationErrors });

                var classStructure = _csharpSynthesisService.SynthesizeTrait(rootNode, traitName);
                var yamlStructure = _yamlSynthesisService.SynthesizeActor(rootNode, classStructure, actorName);
                var yamlCode = _yamlWriter.WriteYaml(yamlStructure);

                // --- YAML Hozzáfűzés (Overwrite/Append) ---
                // Mivel a "rules" fájlba több unit is kerülhet, itt nem felülírjuk az egész fájlt, 
                // hanem HOZZÁFŰZZÜK a végéhez.
                string updatedFilePath = _fileWriter.AppendToYamlRules(yamlCode);

                return Ok(new
                {
                    Message = "Unit successfully added to rules file.",
                    UpdatedFile = updatedFilePath,
                    Code = yamlCode
                });
            }
            catch (Exception ex) { return StatusCode(500, new { Error = ex.Message }); }
        }
    }
}