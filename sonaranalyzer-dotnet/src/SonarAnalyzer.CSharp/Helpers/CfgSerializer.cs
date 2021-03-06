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

using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using SonarAnalyzer.SymbolicExecution.ControlFlowGraph;

namespace SonarAnalyzer.Helpers
{
    internal static class CfgSerializer
    {
        public static string Serialize(string methodName, IControlFlowGraph cfg)
        {
            var stringBuilder = new StringBuilder();
            using (var writer = new StringWriter(stringBuilder))
            {
                Serialize(methodName, cfg, writer);
            }
            return stringBuilder.ToString();
        }

        public static void Serialize(string methodName, IControlFlowGraph cfg, TextWriter writer)
        {
            new CfgWalker(new DotWriter(writer)).Visit(methodName, cfg);
        }

        private class CfgWalker
        {
            private readonly BlockIdMap blockId = new BlockIdMap();
            private readonly DotWriter writer;

            public CfgWalker(DotWriter writer)
            {
                this.writer = writer;
            }

            public void Visit(string methodName, IControlFlowGraph cfg)
            {
                writer.WriteGraphStart(methodName);

                foreach (var block in cfg.Blocks)
                {
                    Visit(block);
                }

                writer.WriteGraphEnd();
            }

            private void Visit(Block block)
            {
                Func<Block, string> getLabel = b => string.Empty;

                if (block is BinaryBranchBlock binaryBranchBlock)
                {
                    WriteNode(block, binaryBranchBlock.BranchingNode);
                    // Add labels to the binary branch block successors
                    getLabel = b =>
                    {
                        if (b == binaryBranchBlock.TrueSuccessorBlock)
                        {
                            return bool.TrueString;
                        }
                        else if (b == binaryBranchBlock.FalseSuccessorBlock)
                        {
                            return bool.FalseString;
                        }
                        return string.Empty;
                    };
                }
                else if (block is BranchBlock branchBlock)
                {
                    WriteNode(block, branchBlock.BranchingNode);
                }
                else if (block is ExitBlock exitBlock)
                {
                    WriteNode(block);
                }
                else if (block is ForeachCollectionProducerBlock foreachBlock)
                {
                    WriteNode(foreachBlock, foreachBlock.ForeachNode);
                }
                else if (block is ForInitializerBlock forBlock)
                {
                    WriteNode(forBlock, forBlock.ForNode);
                }
                else if (block is JumpBlock jumpBlock)
                {
                    WriteNode(jumpBlock, jumpBlock.JumpNode);
                }
                else if (block is LockBlock lockBlock)
                {
                    WriteNode(lockBlock, lockBlock.LockNode);
                }
                else if (block is UsingEndBlock usingBlock)
                {
                    WriteNode(usingBlock, usingBlock.UsingStatement);
                }
                else
                {
                    WriteNode(block);
                }
                WriteEdges(block, getLabel);
            }

            private void WriteNode(Block block, SyntaxNode terminator = null)
            {
                var header = block.GetType().Name.SplitCamelCaseToWords().First().ToUpperInvariant();
                if (terminator != null)
                {
                    // shorten the text
                    var terminatorType = terminator.GetType().Name.Replace("Syntax", string.Empty);

                    header += ":" + terminatorType;
                }
                writer.WriteNode(blockId.Get(block), header, block.Instructions.Select(i => i.ToString()).ToArray());
            }

            private void WriteEdges(Block block, Func<Block, string> getLabel)
            {
                foreach (var successor in block.SuccessorBlocks)
                {
                    writer.WriteEdge(blockId.Get(block), blockId.Get(successor), getLabel(successor));
                }
            }
        }
    }
}
