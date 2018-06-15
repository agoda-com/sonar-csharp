﻿/*
 * SonarAnalyzer for .NET
 * Copyright (C) 2015-2018 SonarSource SA
 * mailto: contact AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SonarAnalyzer.Helpers;
using SonarAnalyzer.Protobuf.Ucfg;
using SonarAnalyzer.SymbolicExecution.ControlFlowGraph;
using UcfgLocation = SonarAnalyzer.Protobuf.Ucfg.Location;

namespace SonarAnalyzer.Security.Ucfg
{
    public class UniversalControlFlowGraphBuilder
    {
        /// <summary>
        /// The string constant representation in the Sonar Security engine (Java part). When
        /// an instruction receives or returns a type that is not string we use this instead
        /// of a variable.
        /// </summary>
        private static readonly Expression ConstantExpression = new Expression
        {
            Const = new Constant { Value = "\"\"" }
        };

        private readonly BlockIdMap blockId = new BlockIdMap();

        public UCFG Build(SemanticModel semanticModel, SyntaxNode syntaxNode, IMethodSymbol methodSymbol, IControlFlowGraph cfg)
        {
            var ucfg = new UCFG
            {
                MethodId = GetMethodId(methodSymbol),
                Location = GetLocation(syntaxNode),
            };

            ucfg.BasicBlocks.AddRange(cfg.Blocks.Select(b => CreateBasicBlock(b, semanticModel)));
            ucfg.Parameters.AddRange(methodSymbol.GetParameters().Select(p => p.Name));

            if (syntaxNode is BaseMethodDeclarationSyntax methodDeclaration &&
                EntryPointRecognizer.IsEntryPoint(methodSymbol))
            {
                var entryPointBlock = CreateEntryPointBlock(semanticModel, methodDeclaration, methodSymbol, blockId.Get(cfg.EntryBlock));
                ucfg.BasicBlocks.Add(entryPointBlock);
                ucfg.Entries.Add(entryPointBlock.Id);
            }
            else
            {
                ucfg.Entries.Add(blockId.Get(cfg.EntryBlock));
            }
            return ucfg;
        }

        private BasicBlock CreateBasicBlock(Block block, SemanticModel semanticModel)
        {
            var basicBlock = new BasicBlock
            {
                Id = blockId.Get(block),
            };

            var instructionBuilder = new InstructionBuilder(semanticModel, basicBlock);

            foreach (var instruction in block.Instructions)
            {
                instructionBuilder.BuildInstruction(instruction);
            }

            if (block is JumpBlock jump)
            {
                instructionBuilder.BuildInstruction(jump.JumpNode);
            }

            if (block is ExitBlock exit)
            {
                basicBlock.Ret = new Return { ReturnedExpression = ConstantExpression };
            }

            if (basicBlock.TerminatorCase == BasicBlock.TerminatorOneofCase.None)
            {
                // No return was created from JumpBlock or ExitBlock, wire up the successor blocks
                basicBlock.Jump = new Jump();
                basicBlock.Jump.Destinations.AddRange(block.SuccessorBlocks.Select(blockId.Get));
            }

            return basicBlock;
        }

        private BasicBlock CreateEntryPointBlock(SemanticModel semanticModel, BaseMethodDeclarationSyntax methodDeclaration,
            IMethodSymbol methodSymbol, string currentEntryBlockId)
        {
            var basicBlock = new BasicBlock
            {
                Id = blockId.Get(new TemporaryBlock()),
                Jump = new Jump
                {
                    Destinations = { currentEntryBlockId },
                }
            };

            var instructionBuilder = new InstructionBuilder(semanticModel, basicBlock);

            instructionBuilder.CreateEntryPointInstruction(methodDeclaration);

            foreach (var parameter in methodSymbol.Parameters)
            {
                instructionBuilder.CreateAttributeInstructions(parameter);
            }

            return basicBlock;
        }

        /// <summary>
        /// Returns UCFG Location that represents the location of the provided SyntaxNode
        /// in SonarQube coordinates - 1-based line numbers and 0-based columns (line offsets).
        /// Roslyn coordinates are 0-based.
        /// </summary>
        private static UcfgLocation GetLocation(SyntaxNode syntaxNode)
        {
            var location = syntaxNode.GetLocation();
            var lineSpan = location.GetLineSpan();
            return new UcfgLocation
            {
                FileId = location.SourceTree.FilePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                StartLineOffset = lineSpan.StartLinePosition.Character,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndLineOffset = lineSpan.EndLinePosition.Character - 1,
            };
        }

        public static string GetMethodId(IMethodSymbol methodSymbol)
        {
            switch (methodSymbol?.MethodKind)
            {
                case MethodKind.ExplicitInterfaceImplementation:
                    return GetMethodId(methodSymbol.ExplicitInterfaceImplementations.First());

                case MethodKind.ReducedExtension:
                    return methodSymbol.ReducedFrom.ToDisplayString();

                default:
                    return methodSymbol?.OriginalDefinition?.ToDisplayString() ?? KnownMethodId.Unknown;
            }
        }

        private class BlockBuilder
        {
            private readonly BasicBlock basicBlock;

            public BlockBuilder(BasicBlock basicBlock)
            {
                this.basicBlock = basicBlock;
            }

            public Expression AddAssignment(SyntaxNode syntaxNode, string variable, Expression argument)
            {
                var instruction = CreateInstruction(syntaxNode, KnownMethodId.Assignment, variable, argument);
                return CreateVariableExpression(instruction.Variable);
            }

            public Expression AddConcatenation(SyntaxNode syntaxNode, params Expression[] arguments)
            {
                var instruction = CreateInstruction(syntaxNode, KnownMethodId.Concatenation, CreateTempVariable(), arguments);
                return CreateVariableExpression(instruction.Variable);
            }

            public Expression AddMethodCall(SyntaxNode invocation, IMethodSymbol methodSymbol, params Expression[] arguments)
            {
                // Add instruction to UCFG only when the method accepts/returns string,
                // or when at least one of its arguments is known to be a string.
                // Since we generate Const expressions for everything that is not
                // a string, checking if the arguments are Var expressions should
                // be enough to ensure they are strings.
                if (!AcceptsOrReturnsString(methodSymbol) &&
                    !arguments.Any(IsVariable))
                {
                    return ConstantExpression;
                }

                var instruction = CreateInstruction(
                    invocation,
                    methodId: GetMethodId(methodSymbol),
                    variable: CreateTempVariable(),
                    arguments: arguments);

                return methodSymbol.ReturnType.Is(KnownType.System_String)
                    ? CreateVariableExpression(instruction.Variable)
                    : ConstantExpression;

                bool IsVariable(Expression expression) =>
                    expression.Var != null;
            }

            public void CreateAttributeInstructions(IParameterSymbol parameter)
            {
                foreach (var attribute in parameter.GetAttributes().Where(a => a.AttributeConstructor != null))
                {
                    var attributeVariable = CreateTempVariable();

                    CreateInstruction(
                        syntaxNode: attribute.ApplicationSyntaxReference.GetSyntax(),
                        methodId: GetMethodId(attribute.AttributeConstructor),
                        variable: attributeVariable);

                    CreateInstruction(
                        syntaxNode: attribute.ApplicationSyntaxReference.GetSyntax(),
                        methodId: KnownMethodId.Annotation,
                        variable: parameter.Name,
                        arguments: CreateVariableExpression(attributeVariable));
                }
            }

            public void CreateEntryPointInstruction(BaseMethodDeclarationSyntax methodDeclaration)
            {
                CreateInstruction(
                    syntaxNode: methodDeclaration,
                    methodId: KnownMethodId.EntryPoint,
                    variable: CreateTempVariable(),
                    arguments: methodDeclaration.ParameterList.Parameters
                        .Select(GetParameterName)
                        .Select(CreateVariableExpression)
                        .ToArray());

                string GetParameterName(ParameterSyntax parameter) =>
                    parameter.Identifier.ValueText;
            }


            private Instruction CreateInstruction(SyntaxNode syntaxNode, string methodId, string variable, params Expression[] arguments)
            {
                var instruction = new Instruction
                {
                    Location = GetLocation(syntaxNode),
                    MethodId = methodId,
                    Variable = variable ?? ConstantExpression.Const.Value,
                };
                instruction.Args.AddRange(arguments);
                basicBlock.Instructions.Add(instruction);
                return instruction;
            }

            public static Expression CreateVariableExpression(string name) =>
                new Expression { Var = new Variable { Name = name } };

            private int tempVariablesCounter;

            private string CreateTempVariable() =>
                $"%{tempVariablesCounter++}";
        }

        private class InstructionBuilder
        {
            private readonly Dictionary<SyntaxNode, Expression> nodeExpressionMap = new Dictionary<SyntaxNode, Expression>();
            private readonly SemanticModel semanticModel;
            private readonly BlockBuilder blockBuilder;

            public InstructionBuilder(SemanticModel semanticModel, BlockBuilder blockBuilder)
            {
                this.semanticModel = semanticModel;
                this.blockBuilder = blockBuilder;
            }

            public Expression BuildInstruction(SyntaxNode syntaxNode) =>
                nodeExpressionMap.GetOrAdd(syntaxNode.RemoveParentheses(), BuildInstructionImpl);

            private Expression BuildInstructionImpl(SyntaxNode syntaxNode)
            {
                switch (syntaxNode.Kind())
                {
                    case SyntaxKind.AddExpression:
                        return BuildBinaryExpression((BinaryExpressionSyntax)syntaxNode);

                    case SyntaxKind.SimpleAssignmentExpression:
                        return BuildAssignment((AssignmentExpressionSyntax)syntaxNode);

                    case SyntaxKind.InvocationExpression:
                        return BuildInvocation((InvocationExpressionSyntax)syntaxNode);

                    case SyntaxKind.IdentifierName:
                        return BuildIdentifierName((IdentifierNameSyntax)syntaxNode);

                    case SyntaxKind.VariableDeclarator:
                        BuildVariableDeclarator((VariableDeclaratorSyntax)syntaxNode);
                        return null;

                    case SyntaxKind.ReturnStatement:
                        BuildReturn((ReturnStatementSyntax)syntaxNode);
                        return null;

                    case SyntaxKind.ObjectCreationExpression:
                        return BuildObjectCreation((ObjectCreationExpressionSyntax)syntaxNode);

                    default:
                        // do nothing
                        return ConstantExpression;
                }
            }

            private Expression BuildObjectCreation(ObjectCreationExpressionSyntax objectCreation)
            {
                var ctorSymbol = GetSymbol(objectCreation) as IMethodSymbol;
                if (ctorSymbol == null)
                {
                    return ConstantExpression;
                }

                var arguments = BuildArguments(objectCreation.ArgumentList).ToArray();

                return blockBuilder.AddMethodCall(objectCreation, ctorSymbol, arguments);
            }

            private Expression BuildIdentifierName(IdentifierNameSyntax identifier)
            {
                var identifierSymbol = GetSymbol(identifier);

                if (identifierSymbol is IPropertySymbol property)
                {
                    return blockBuilder.AddMethodCall(identifier, property.GetMethod);
                }
                else if (IsLocalVarOrParameterOfTypeString(identifierSymbol))
                {
                    return BlockBuilder.CreateVariableExpression(identifierSymbol.Name);
                }
                else
                {
                    return ConstantExpression;
                }
            }

            private void BuildVariableDeclarator(VariableDeclaratorSyntax variableDeclarator)
            {
                if (variableDeclarator.Initializer == null)
                {
                    return;
                }

                var variable = semanticModel.GetDeclaredSymbol(variableDeclarator);
                if (IsLocalVarOrParameterOfTypeString(variable))
                {
                    blockBuilder.AddAssignment(variableDeclarator, variable.Name, BuildInstruction(variableDeclarator.Initializer.Value));
                }
            }

            private Expression BuildBinaryExpression(BinaryExpressionSyntax binaryExpression)
            {
                return blockBuilder.AddConcatenation(binaryExpression,
                    new[] { BuildInstruction(binaryExpression.Right), BuildInstruction(binaryExpression.Left) });
            }

            private void BuildReturn(ReturnStatementSyntax returnStatement)
            {
                basicBlock.Ret = new Return
                {
                    Location = GetLocation(returnStatement),
                    ReturnedExpression = returnStatement.Expression != null
                        ? BuildInstruction(returnStatement.Expression)
                        : ConstantExpression,
                };
            }

            private Expression BuildInvocation(InvocationExpressionSyntax invocation)
            {
                var methodSymbol = GetSymbol(invocation) as IMethodSymbol;
                if (methodSymbol == null)
                {
                    return ConstantExpression;
                }

                // The arguments are built in advance to allow nested instructions
                // to be added, regardless of whether the current invocation is going
                // to be added to the UCFG or not. For example: LogStatus(StoreInDb(str1 + str2));
                // should add 'str1 + str2' and 'StoreInDb(string)', but not 'void LogStatus(int)'
                var arguments = BuildArguments(invocation, methodSymbol).ToArray();

                return blockBuilder.AddMethodCall(invocation, methodSymbol, arguments);
            }

            private IEnumerable<Expression> BuildArguments(ArgumentListSyntax argumentList)
            {
                if (argumentList == null)
                {
                    yield break;
                }

                foreach (var argument in argumentList.Arguments)
                {
                    yield return BuildInstruction(argument.Expression);
                }
            }

            private IEnumerable<Expression> BuildArguments(InvocationExpressionSyntax invocation, IMethodSymbol methodSymbol)
            {
                if (IsInstanceMethodOnString(methodSymbol) ||
                    IsExtensionMethodCalledAsExtension(methodSymbol))
                {
                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        // add the string to the beginning of the arguments list
                        yield return BuildInstruction(memberAccess.Expression);
                    }
                }

                if (invocation.ArgumentList == null)
                {
                    yield break;
                }

                foreach (var argument in invocation.ArgumentList.Arguments)
                {
                    yield return BuildInstruction(argument.Expression);
                }
            }

            private Expression BuildAssignment(AssignmentExpressionSyntax assignment)
            {
                var left = GetSymbol(assignment.Left);

                var right = BuildInstruction(assignment.Right);

                if (IsLocalVarOrParameterOfTypeString(left))
                {
                    return blockBuilder.AddAssignment(assignment, left.Name, right);
                }
                else if (left is IPropertySymbol property &&
                    property.SetMethod != null)
                {
                    return blockBuilder.AddMethodCall(assignment, property.SetMethod, right);
                }
                else
                {
                    return ConstantExpression;
                }
            }

            private ISymbol GetSymbol(SyntaxNode syntaxNode) =>
                semanticModel.GetSymbolInfo(syntaxNode).Symbol;

        }

        private static bool IsExtensionMethodCalledAsExtension(IMethodSymbol methodSymbol) =>
            methodSymbol.ReducedFrom != null;

        private static bool IsInstanceMethodOnString(IMethodSymbol methodSymbol) =>
            methodSymbol.ContainingType.Is(KnownType.System_String) && !methodSymbol.IsStatic;

        private static bool IsLocalVarOrParameterOfTypeString(ISymbol symbol) =>
            symbol is ILocalSymbol local && local.Type.Is(KnownType.System_String) ||
            symbol is IParameterSymbol parameter && parameter.Type.Is(KnownType.System_String);

        private static bool AcceptsOrReturnsString(IMethodSymbol methodSymbol) =>
            methodSymbol.ReturnType.Is(KnownType.System_String) ||
            methodSymbol.Parameters.Any(p => p.Type.Is(KnownType.System_String));

        private class BlockIdMap
        {
            private readonly Dictionary<Block, string> map = new Dictionary<Block, string>();
            private int counter;

            public string Get(Block cfgBlock) =>
                map.GetOrAdd(cfgBlock, b => $"{counter++}");
        }
    }
}
