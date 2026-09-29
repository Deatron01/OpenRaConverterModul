# OpenRaConverterModul

Turns behaviour designed as a **decision tree** into working **OpenRA** mod code.

You describe what a unit should do as a tree of conditions and actions in JSON. The
converter validates the tree, then generates the C# trait class and the YAML rule that
OpenRA needs, and writes them straight into your mod folder.

It is one module of the **OE_Lagrange** project. The other tools (entity creator service,
database schemas, environment manager) live in
[OE_Lagrange_Side_Projects](https://github.com/Deatron01/OE_Lagrange_Side_Projects).

## Example

This tree makes a unit retreat to base when it is badly hurt:

```json
{
  "id": "root",
  "condition": "Health < 50%",
  "children": [
    { "id": "flee", "action": "Move(Base)" }
  ]
}
```

Sent to `POST /api/Synthesis/generate-csharp?traitName=FleeBehavior`, it produces a
`FleeBehavior` trait with its `FleeBehaviorInfo` class, a health check against
`self.Trait<Health>().HP`, and a queued `Move` activity. The integration test in
`OpenRA.Converter.Tests/Integration/EndToEndTests.cs` checks exactly this.

## How it works

```
decision tree (JSON)
      │
      ▼
 DecisionTreeService      parse, then validate structure and conditions
      │                   (every node has an id, no leaf with children, no dead ends,
      │                    conditions like  Health < 50%  or  Not Cloaked == true)
      ▼
 ReferenceRegistry        known OpenRA traits and weapons, loaded through the API
      │
      ├─► TraitSynthesisService ─► CSharpCodeWriter ─► Traits/<Name>.cs
      └─► YamlSynthesisService  ─► YamlCodeWriter   ─► rules/*.yaml  (appended)
```

## Projects

| Project | Role |
|---|---|
| `OpenRA.Converter.Api` | ASP.NET Core Web API with Swagger UI |
| `OpenRA.Converter.Core` | Models (decision nodes, trait and weapon schemas, C# and YAML structures) and interfaces |
| `OpenRA.Converter.Infrastructure` | Parsing, validation, code synthesis and file writing |
| `OpenRA.Converter.Tests` | xUnit and Moq tests, unit plus an end-to-end pipeline test |

## API

| Method | Endpoint | What it does |
|---|---|---|
| `POST` | `/api/References/traits` | Load trait definitions into the registry |
| `POST` | `/api/References/weapons` | Load weapon definitions |
| `GET` | `/api/References/traits/{name}` | Look up one trait |
| `GET` | `/api/References/weapons/{type}` | Look up one weapon |
| `POST` | `/api/DecisionTrees/parse` | Parse and validate a tree; returns the errors if any |
| `GET` | `/api/DecisionTrees/debug-condition?condition=…` | Show how a single condition string is parsed |
| `POST` | `/api/Synthesis/generate-csharp?traitName=…` | Generate the C# trait |
| `POST` | `/api/Synthesis/generate-yaml?actorName=…&traitName=…` | Generate the YAML rule |

## Running it

Requires the .NET 8 SDK and a local copy of OpenRA to write into.

1. Point the output paths in `OpenRA.Converter.Api/appsettings.json` at your mod:

   ```json
   "ConverterSettings": {
     "CSharpOutputPath": "<OpenRA>/OpenRA.Mods.Common/Traits/OETraits",
     "YamlRulesFilePath": "<OpenRA>/mods/ra/rules/infantry.yaml"
   }
   ```

2. Start the API and open the Swagger UI at `/swagger`:

   ```bash
   dotnet run --project OpenRaConverterModul/OpenRA.Converter.Api
   ```

3. Run the tests:

   ```bash
   dotnet test OpenRaConverterModul/OpenRaConverterModul.sln
   ```
