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
            // --- 1. DEFINE ALL REQUIRED NAMESPACES ---
            var requiredUsings = new List<string> {
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "OpenRA",
                "OpenRA.Traits",
                "OpenRA.Mods.Common.Traits",
                "OpenRA.Mods.Common.Activities",
                "OpenRA.Primitives"
            };

            // --- 2. INFO CLASS SETUP ---
            var infoClass = new CsClass
            {
                Name = $"{traitName}Info",
                Inherits = "ConditionalTraitInfo",
                Usings = new List<string>(requiredUsings)
            };

            infoClass.Fields.Add(new CsField
            {
                Name = "Period",
                Type = "int",
                IsExposedToYaml = true,
                InitialValue = "10",
                Description = "Ticks between AI decision updates."
            });

            var createMethod = new CsMethod
            {
                Name = "Create",
                ReturnType = "object",
                AccessModifier = "public override"
            };
            createMethod.Parameters.Add(new CsParameter("ActorInitializer", "init"));
            createMethod.BodyLines.Add($"return new {traitName}(init.Self, this);");
            infoClass.Methods.Add(createMethod);

            // --- 3. LOGIC CLASS SETUP ---
            var logicClass = new CsClass
            {
                Name = traitName,
                Inherits = $"ConditionalTrait<{traitName}Info>",
                PairedInfoClass = infoClass
                // Fix: Ensure generating Usings in the Logic Class
                ,
                Usings = new List<string>(requiredUsings)
            };

            logicClass.Interfaces.Add("ITick");
            logicClass.Interfaces.Add("INotifyCreated");

            // Define standard dependencies
            logicClass.Fields.Add(new CsField { Name = "_health", Type = "Health", AccessModifier = "private" });
            logicClass.Fields.Add(new CsField { Name = "_mobile", Type = "Mobile", AccessModifier = "private" });
            logicClass.Fields.Add(new CsField { Name = "_attack", Type = "AttackFrontal", AccessModifier = "private" });
            logicClass.Fields.Add(new CsField { Name = "_armament", Type = "Armament", AccessModifier = "private" });

            // Define timer
            logicClass.Fields.Add(new CsField { Name = "_ticksRemaining", Type = "int", AccessModifier = "private" });

            // Add Standard Methods
            AddBoilerplateMethods(logicClass, traitName);

            // --- 4. GENERATE DECISION LOGIC ---
            var tickMethod = logicClass.Methods.First(m => m.Name == "Tick");

            tickMethod.BodyLines.Add("// Ensure we have a body to control");
            tickMethod.BodyLines.Add("if (self.IsDead) return;");
            tickMethod.BodyLines.Add("");

            ProcessNode(rootNode, tickMethod.BodyLines, 0, logicClass, infoClass);

            return logicClass;
        }

        private void AddBoilerplateMethods(CsClass logicClass, string traitName)
        {
            // Constructor
            var ctor = new CsMethod { Name = traitName, ReturnType = "", AccessModifier = "public" };
            ctor.Parameters.Add(new CsParameter("Actor", "self"));
            ctor.Parameters.Add(new CsParameter($"{traitName}Info", "info"));
            ctor.BodyLines.Add(": base(info)");
            ctor.BodyLines.Add("_ticksRemaining = info.Period;");
            logicClass.Methods.Add(ctor);

            // Created
            var created = new CsMethod { Name = "Created", ReturnType = "void", ExplicitInterfaceImplementation = "INotifyCreated" };
            created.Parameters.Add(new CsParameter("Actor", "self"));
            created.BodyLines.Add("_health = self.TraitOrDefault<Health>();");
            created.BodyLines.Add("_mobile = self.TraitOrDefault<Mobile>();");
            created.BodyLines.Add("_attack = self.TraitOrDefault<AttackFrontal>();");
            created.BodyLines.Add("_armament = self.TraitOrDefault<Armament>();");
            logicClass.Methods.Add(created);

            // Tick
            var tick = new CsMethod { Name = "Tick", ReturnType = "void", ExplicitInterfaceImplementation = "ITick" };
            tick.Parameters.Add(new CsParameter("Actor", "self"));
            tick.BodyLines.Add("if (IsTraitDisabled) return;");
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
                    GenerateActionBlock(node.Action, bodyLines, indentLevel + 1, logicClass, infoClass);

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
                    GenerateActionBlock(node.Action, bodyLines, indentLevel, logicClass, infoClass);

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

            if (a.Equals("InAttackRange", StringComparison.OrdinalIgnoreCase) && b.Equals("NotInAttackRange", StringComparison.OrdinalIgnoreCase)) return true;
            if (b.Equals("InAttackRange", StringComparison.OrdinalIgnoreCase) && a.Equals("NotInAttackRange", StringComparison.OrdinalIgnoreCase)) return true;

            if (a.Replace("Not", "") == b.Replace("Not", "") && a != b) return true;

            return false;
        }

        private string MapConditionToCSharp(string rawCondition, CsClass logicClass, CsClass infoClass)
        {
            var parsed = _decisionTreeService.ParseConditionString(rawCondition);
            string expression;
            string varName = parsed.Variable.Trim();

            if (varName.Equals("Health", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("Health");
                string op = parsed.Operator ?? "<";
                string valStr = parsed.Value?.Replace("%", "") ?? "0";

                if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                {
                    int percent = (int)val;
                    expression = $"_health != null && _health.HP {op} (_health.MaxHP * {percent} / 100)";
                }
                else
                {
                    string paramName = EnsureField(infoClass, valStr, "int", "50");
                    expression = $"_health != null && _health.HP {op} (_health.MaxHP * Info.{paramName} / 100)";
                }
            }
            else if (varName.Equals("EnemyVisible", StringComparison.OrdinalIgnoreCase))
            {
                expression = "self.World.FindActorsInCircle(self.CenterPosition, WDist.FromCells(10)).Any(a => !a.IsDead && a.AppearsHostileTo(self))";
            }
            else if (varName.Equals("InAttackRange", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("AttackFrontal");
                // FIX: Replaced 'IsAttacking' with 'IsAiming' which is the standard AttackBase property
                expression = "(_attack != null && _attack.IsAiming)";
            }
            else if (varName.Equals("NotInAttackRange", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("AttackFrontal");
                expression = "!(_attack != null && _attack.IsAiming)";
            }
            else if (varName.Equals("HasRocket", StringComparison.OrdinalIgnoreCase) ||
                     varName.Equals("HasFlamethrower", StringComparison.OrdinalIgnoreCase) ||
                     varName.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
            {
                // FIX: Armament doesn't have Ammo. We check the AmmoPool trait.
                logicClass.RequiredYamlInherits.Add("AmmoPool");
                expression = "self.TraitsImplementing<AmmoPool>().Any(x => x.HasAmmo)";
            }
            else
            {
                string paramName = EnsureField(infoClass, varName, "bool", "false");
                expression = $"Info.{paramName}";
            }

            if (parsed.IsNegated) return $"!({expression})";
            return expression;
        }

        private void GenerateActionBlock(string rawAction, List<string> bodyLines, int indentLevel, CsClass logicClass, CsClass infoClass)
        {
            string indent = new string('\t', indentLevel);
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

            // --- WAIT ---
            if (method.Equals("Wait", StringComparison.OrdinalIgnoreCase))
            {
                int ticks = 25;
                if (!string.IsNullOrEmpty(firstArg) && int.TryParse(firstArg, out int t)) ticks = t;

                bodyLines.Add($"{indent}self.CancelActivity();");
                bodyLines.Add($"{indent}self.QueueActivity(new Wait({ticks}));");
                return;
            }

            // --- ATTACK ---
            if (method.Contains("Attack", StringComparison.OrdinalIgnoreCase) ||
                method.Contains("Clear", StringComparison.OrdinalIgnoreCase) ||
                method.Contains("Hunt", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("Armament");
                logicClass.RequiredYamlInherits.Add("AttackFrontal");
                logicClass.RequiredYamlInherits.Add("Mobile");

                if (method.Contains("Hunt", StringComparison.OrdinalIgnoreCase))
                {
                    bodyLines.Add($"{indent}self.CancelActivity();");
                    bodyLines.Add($"{indent}self.QueueActivity(new Hunt(self));");
                    return;
                }

                // Smart Target Finding
                bodyLines.Add($"{indent}var enemy = self.World.FindActorsInCircle(self.CenterPosition, WDist.FromCells(10))");
                bodyLines.Add($"{indent}\t.Where(a => !a.IsDead && a.AppearsHostileTo(self))");
                bodyLines.Add($"{indent}\t.OrderBy(a => (a.CenterPosition - self.CenterPosition).LengthSquared)");
                bodyLines.Add($"{indent}\t.FirstOrDefault();");

                bodyLines.Add($"{indent}if (enemy != null)");
                bodyLines.Add($"{indent}{{");
                bodyLines.Add($"{indent}\tself.CancelActivity();");
                // FIX: Corrected Attack constructor signature (Actor, Target, allowMove, forceAttack)
                bodyLines.Add($"{indent}\tself.QueueActivity(new Attack(self, Target.FromActor(enemy), true, false));");
                bodyLines.Add($"{indent}}}");
                bodyLines.Add($"{indent}else");
                bodyLines.Add($"{indent}{{");
                bodyLines.Add($"{indent}\tself.CancelActivity();");
                // FIX: Use Hunt for generic aggressive move instead of AttackMoveActivity (which needs a specific delegate/target)
                logicClass.RequiredYamlInherits.Add("Hunt");
                bodyLines.Add($"{indent}\tself.QueueActivity(new Hunt(self));");
                bodyLines.Add($"{indent}}}");
                return;
            }

            // --- MOVE ---
            if (method.Equals("Move", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("Retreat", StringComparison.OrdinalIgnoreCase))
            {
                logicClass.RequiredYamlInherits.Add("Mobile");

                bodyLines.Add($"{indent}if (_mobile != null)");
                bodyLines.Add($"{indent}{{");
                bodyLines.Add($"{indent}\tvar enemy = self.World.FindActorsInCircle(self.CenterPosition, WDist.FromCells(10))");
                bodyLines.Add($"{indent}\t\t.Where(a => !a.IsDead && a.AppearsHostileTo(self))");
                bodyLines.Add($"{indent}\t\t.OrderBy(a => (a.CenterPosition - self.CenterPosition).LengthSquared)");
                bodyLines.Add($"{indent}\t\t.FirstOrDefault();");

                bodyLines.Add($"{indent}\tCVec retreatDir = new CVec(self.World.SharedRandom.Next(-2, 3), self.World.SharedRandom.Next(-2, 3));");
                bodyLines.Add($"{indent}\tif (enemy != null)");
                bodyLines.Add($"{indent}\t{{");
                bodyLines.Add($"{indent}\t\tvar vecToEnemy = enemy.Location - self.Location;");
                bodyLines.Add($"{indent}\t\tif (vecToEnemy.LengthSquared > 0) retreatDir = -1 * (vecToEnemy / vecToEnemy.Length) * 3;");
                bodyLines.Add($"{indent}\t}}");

                bodyLines.Add($"{indent}\tvar dest = self.Location + retreatDir;");
                bodyLines.Add($"{indent}\tself.CancelActivity();");
                bodyLines.Add($"{indent}\tself.QueueActivity(_mobile.MoveTo(dest, 1));");
                bodyLines.Add($"{indent}}}");
                return;
            }

            bodyLines.Add($"{indent}// TODO: Implement Custom Action -> {rawAction}");
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