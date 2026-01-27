using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using OpenRA.Converter.Core.Interfaces;
using OpenRA.Converter.Core.Models.CodeStructure;
using OpenRA.Converter.Core.Models.DecisionTree;

namespace OpenRA.Converter.Infrastructure.Services
{
    public class TraitSynthesisService : ITraitSynthesisService
    {
        private readonly IDecisionTreeService _decisionTreeService;
        private readonly IReferenceRegistry _registry;
        private static readonly Regex ActionArgsRegex = new Regex(@"\((.*?)\)");

        public TraitSynthesisService(IDecisionTreeService decisionTreeService, IReferenceRegistry registry)
        {
            _decisionTreeService = decisionTreeService;
            _registry = registry;
        }

        public CsClass SynthesizeTrait(DecisionNode rootNode, string traitName)
        {
            // 1. Info Class
            var infoClass = new CsClass
            {
                Name = $"{traitName}Info",
                Inherits = "ConditionalTraitInfo",
                Usings = new List<string> {
                    "OpenRA",                      // Fix: Added for WDist
                    "OpenRA.Traits",
                    "OpenRA.Mods.Common.Traits",
                    "OpenRA.Mods.Common.Activities",
                    "OpenRA.Primitives"
                }
            };

            infoClass.Fields.Add(new CsField
            {
                Name = "Period",
                Type = "int",
                IsExposedToYaml = true,
                InitialValue = "25",
                Description = "Time in ticks to wait between checks."
            });

            // Create Override
            var createMethod = new CsMethod
            {
                Name = "Create",
                ReturnType = "object",
                AccessModifier = "public override"
            };
            createMethod.Parameters.Add(new CsParameter("ActorInitializer", "init"));
            createMethod.BodyLines.Add($"return new {traitName}(init.Self, this);");
            infoClass.Methods.Add(createMethod);

            // 2. Logic Class
            var logicClass = new CsClass
            {
                Name = traitName,
                Inherits = $"ConditionalTrait<{traitName}Info>",
                PairedInfoClass = infoClass
            };

            logicClass.Interfaces.Add("ITick");
            logicClass.Interfaces.Add("INotifyCreated");

            // Add standard trait dependencies that logic might use
            AddStandardDependencies(logicClass);

            AddBoilerplateMethods(logicClass, traitName);

            // 3. Process Logic
            var tickMethod = logicClass.Methods.First(m => m.Name == "Tick");

            // Use Smart Branching
            ProcessNode(rootNode, tickMethod.BodyLines, 0, logicClass, infoClass);

            return logicClass;
        }

        private void AddStandardDependencies(CsClass logicClass)
        {
            // Define fields for common traits we likely need for logic
            logicClass.Fields.Add(new CsField { Name = "_health", Type = "Health", AccessModifier = "private" });
            logicClass.Fields.Add(new CsField { Name = "_mobile", Type = "Mobile", AccessModifier = "private" });
            logicClass.Fields.Add(new CsField { Name = "_attack", Type = "AttackFrontal", AccessModifier = "private" });
            logicClass.Fields.Add(new CsField { Name = "_armament", Type = "Armament", AccessModifier = "private" });
        }

        private void AddBoilerplateMethods(CsClass logicClass, string traitName)
        {
            // Ctor
            var ctor = new CsMethod { Name = traitName, ReturnType = "", AccessModifier = "public" };
            ctor.Parameters.Add(new CsParameter("Actor", "self"));
            ctor.Parameters.Add(new CsParameter($"{traitName}Info", "info"));
            ctor.BodyLines.Add(": base(info)");
            ctor.BodyLines.Add("_ticksRemaining = info.Period;");
            logicClass.Methods.Add(ctor);

            // Field for timer
            logicClass.Fields.Add(new CsField { Name = "_ticksRemaining", Type = "int", AccessModifier = "private" });

            // Created
            var created = new CsMethod { Name = "Created", ReturnType = "void", ExplicitInterfaceImplementation = "INotifyCreated" };
            created.Parameters.Add(new CsParameter("Actor", "self"));

            // Initialize dependencies
            created.BodyLines.Add("_health = self.TraitOrDefault<Health>();");
            created.BodyLines.Add("_mobile = self.TraitOrDefault<Mobile>();");
            created.BodyLines.Add("_attack = self.TraitOrDefault<AttackFrontal>();");
            created.BodyLines.Add("_armament = self.TraitOrDefault<Armament>();");

            logicClass.Methods.Add(created);

            // Tick
            var tick = new CsMethod { Name = "Tick", ReturnType = "void", ExplicitInterfaceImplementation = "ITick" };
            tick.Parameters.Add(new CsParameter("Actor", "self"));
            tick.BodyLines.Add("if (IsTraitDisabled) return;");

            // Fix: Removed blocking check (if currentActivity != null return) to allow decision tree to interrupt/react.

            // Period Timer
            tick.BodyLines.Add("if (--_ticksRemaining > 0) return;");
            tick.BodyLines.Add("_ticksRemaining = Info.Period;");
            tick.BodyLines.Add("");

            logicClass.Methods.Add(tick);
        }

        private void ProcessNode(DecisionNode node, List<string> bodyLines, int indentLevel, CsClass logicClass, CsClass infoClass, bool skipConditionWrapper = false)
        {
            string indent = new string('\t', indentLevel);

            var trueChildren = new List<DecisionNode>();
            var falseChildren = new List<DecisionNode>();

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (!string.IsNullOrWhiteSpace(node.Condition) && IsNegation(node.Condition, child.Condition))
                        falseChildren.Add(child);
                    else
                        trueChildren.Add(child);
                }
            }

            if (!string.IsNullOrWhiteSpace(node.Condition) && !skipConditionWrapper)
            {
                string conditionCode = MapConditionToCSharp(node.Condition, logicClass, infoClass);
                bodyLines.Add($"{indent}if ({conditionCode})");
                bodyLines.Add($"{indent}{{");

                if (node.IsLeaf && !string.IsNullOrWhiteSpace(node.Action))
                    bodyLines.Add($"{indent}\t{MapActionToCSharp(node.Action, logicClass, infoClass)}");

                ProcessChildList(trueChildren, bodyLines, indentLevel + 1, logicClass, infoClass);

                bodyLines.Add($"{indent}}}");

                if (falseChildren.Any())
                {
                    bodyLines.Add($"{indent}else");
                    bodyLines.Add($"{indent}{{");
                    ProcessChildList(falseChildren, bodyLines, indentLevel + 1, logicClass, infoClass, stripCondition: true);
                    bodyLines.Add($"{indent}}}");
                }
            }
            else
            {
                if (node.IsLeaf && !string.IsNullOrWhiteSpace(node.Action))
                    bodyLines.Add($"{indent}{MapActionToCSharp(node.Action, logicClass, infoClass)}");

                ProcessChildList(node.Children, bodyLines, indentLevel, logicClass, infoClass);
            }
        }

        private void ProcessChildList(List<DecisionNode> nodes, List<string> bodyLines, int indentLevel, CsClass logicClass, CsClass infoClass, bool stripCondition = false)
        {
            if (nodes == null) return;
            string indent = new string('\t', indentLevel);
            var processed = new HashSet<DecisionNode>();

            for (int i = 0; i < nodes.Count; i++)
            {
                var currentNode = nodes[i];
                if (processed.Contains(currentNode)) continue;

                DecisionNode negationNode = null;
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    if (!processed.Contains(nodes[j]) && IsNegation(currentNode.Condition, nodes[j].Condition))
                    {
                        negationNode = nodes[j];
                        break;
                    }
                }

                if (negationNode != null)
                {
                    ProcessNode(currentNode, bodyLines, indentLevel, logicClass, infoClass, skipConditionWrapper: false);
                    processed.Add(currentNode);
                    processed.Add(negationNode);

                    bodyLines.Add($"{indent}else");
                    bodyLines.Add($"{indent}{{");
                    ProcessNode(negationNode, bodyLines, indentLevel + 1, logicClass, infoClass, skipConditionWrapper: true);
                    bodyLines.Add($"{indent}}}");
                }
                else
                {
                    ProcessNode(currentNode, bodyLines, indentLevel, logicClass, infoClass, skipConditionWrapper: stripCondition);
                    processed.Add(currentNode);
                }
            }
        }

        private bool IsNegation(string conditionA, string conditionB)
        {
            if (string.IsNullOrEmpty(conditionA) || string.IsNullOrEmpty(conditionB)) return false;
            var a = conditionA.Trim();
            var b = conditionB.Trim();
            if (a.Equals($"Not {b}", StringComparison.OrdinalIgnoreCase) || b.Equals($"Not {a}", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.Equals($"!{b}", StringComparison.OrdinalIgnoreCase) || b.Equals($"!{a}", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private string MapConditionToCSharp(string rawCondition, CsClass logicClass, CsClass infoClass)
        {
            var parsed = _decisionTreeService.ParseConditionString(rawCondition);
            string expression;

            // Fix: Float culture safety
            if (parsed.Variable.Equals("Health", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("Health");
                string op = parsed.Operator ?? "<";
                string valStr = parsed.Value?.Replace("%", "") ?? "0";

                if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                {
                    string floatVal = (val / 100.0).ToString(CultureInfo.InvariantCulture) + "f";
                    expression = $"_health != null && _health.HP {op} (int)(_health.MaxHP * {floatVal})";
                }
                else
                {
                    string paramName = EnsureField(infoClass, valStr, "int", "50");
                    expression = $"_health != null && _health.HP {op} (int)(_health.MaxHP * (Info.{paramName} / 100f))";
                }
            }
            // Fix: Proper enemy detection logic
            else if (parsed.Variable.Equals("EnemyVisible", StringComparison.OrdinalIgnoreCase))
            {
                expression = "self.World.FindActorsInCircle(self.CenterPosition, WDist.FromCells(10))" +
                             ".Any(a => !a.IsDead && a.AppearsHostileTo(self))";
            }
            // Fix: Dynamic logic for "InAttackRange"
            else if (parsed.Variable.Equals("InAttackRange", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("AttackFrontal");
                expression = "(_attack != null && _attack.IsAttacking)";
            }
            // Fix: Explicitly handle the "NotInAttackRange" string as the negation of the above
            // This prevents creating a static bool in YAML that is always false.
            else if (parsed.Variable.Equals("NotInAttackRange", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("AttackFrontal");
                expression = "!(_attack != null && _attack.IsAttacking)";
            }
            else if (parsed.Variable.Equals("HasRocket", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("Armament");
                expression = "(_armament != null && _armament.Ammo > 0)";
            }
            else
            {
                // Fallback for unknown flags (e.g. CanFlank) - these become config parameters
                string paramName = EnsureField(infoClass, parsed.Variable, "bool", "false");
                expression = $"Info.{paramName}";
            }

            if (parsed.IsNegated) return $"!({expression})";
            return expression;
        }

        private string MapActionToCSharp(string rawAction, CsClass logicClass, CsClass infoClass)
        {
            string cleanAction = rawAction.Trim().TrimEnd(';');
            string method = cleanAction;
            string args = "";

            var match = ActionArgsRegex.Match(cleanAction);
            if (match.Success)
            {
                method = cleanAction.Substring(0, cleanAction.IndexOf('(')).Trim();
                args = match.Groups[1].Value;
            }

            var argList = args.Split(',').Select(a => a.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            string firstArg = argList.FirstOrDefault() ?? "";

            if (method.Equals("Wait", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(firstArg, out int ticks)) return $"self.QueueActivity(new Wait({ticks}));";

                // Fix: Default to 25 if no argument provided, don't create "ParamX"
                if (string.IsNullOrEmpty(firstArg)) return "self.QueueActivity(new Wait(25));";

                string paramName = EnsureField(infoClass, firstArg, "int", "25");
                return $"self.QueueActivity(new Wait(Info.{paramName}));";
            }

            // Fix: Added ClearEnemies handling
            if (method.Equals("ClearEnemies", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("AttackFrontal");
                logicClass.RequiredYamlInherits.Add("Mobile");
                return "self.QueueActivity(new AttackMoveActivity(self, _mobile.MoveTo));";
            }

            if (method.Contains("Attack", StringComparison.OrdinalIgnoreCase) || method.Contains("Hunt"))
            {
                logicClass.RequiredYamlInherits.Add("Armament");
                logicClass.RequiredYamlInherits.Add("AttackFrontal");
                logicClass.RequiredYamlInherits.Add("Mobile");

                if (method.Contains("Hunt")) return "self.QueueActivity(new Hunt(self));";
                return "self.QueueActivity(new AttackMoveActivity(self, _mobile.MoveTo));";
            }

            if (method.Equals("Move", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("Mobile");
                // Fix: Use _mobile safe accessor
                return "if (_mobile != null) self.QueueActivity(_mobile.MoveTo(self.Location, 1)); // Logic placeholder: Move to self";
            }

            return $"// TODO: Implement Action -> {rawAction}";
        }

        private string EnsureField(CsClass infoClass, string rawName, string type, string defaultValue)
        {
            string fieldName = Regex.Replace(rawName, "[^a-zA-Z0-9]", "");
            if (string.IsNullOrEmpty(fieldName)) fieldName = "Param" + infoClass.Fields.Count;
            fieldName = char.ToUpper(fieldName[0]) + fieldName.Substring(1);

            if (!infoClass.Fields.Any(f => f.Name == fieldName))
            {
                infoClass.Fields.Add(new CsField
                {
                    Name = fieldName,
                    Type = type,
                    AccessModifier = "public",
                    InitialValue = defaultValue,
                    IsExposedToYaml = true,
                    Description = $"Auto-generated parameter for {rawName}"
                });
            }
            return fieldName;
        }
    }
}