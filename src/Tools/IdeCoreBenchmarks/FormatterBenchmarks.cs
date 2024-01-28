﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Formatting;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;

namespace IdeCoreBenchmarks
{
    [MemoryDiagnoser]
    public class FormatterBenchmarks
    {
        private readonly int _iterationCount = 5;

        private Document _document;
        private SyntaxFormattingOptions _options;

        [GlobalSetup]
        public void GlobalSetup()
        {
            var roslynRoot = Environment.GetEnvironmentVariable(Program.RoslynRootPathEnvVariableName);
            var csFilePath = Path.Combine(roslynRoot, @"src\Compilers\CSharp\Portable\Generated\Syntax.xml.Syntax.Generated.cs");

            if (!File.Exists(csFilePath))
            {
                throw new ArgumentException();
            }

            // Remove some newlines
            var text = File.ReadAllText(csFilePath).Replace("<auto-generated />", "")
                .Replace($"{{{Environment.NewLine}{Environment.NewLine}", "{")
                .Replace($"}}{Environment.NewLine}{Environment.NewLine}", "}")
                .Replace($"{{{Environment.NewLine}", "{")
                .Replace($"}}{Environment.NewLine}", "}")
                .Replace($";{Environment.NewLine}", ";");

            var projectId = ProjectId.CreateNewId();
            var documentId = DocumentId.CreateNewId(projectId);

            var solution = new AdhocWorkspace().CurrentSolution
                .AddProject(projectId, "ProjectName", "AssemblyName", LanguageNames.CSharp)
                .AddDocument(documentId, "DocumentName", text);

            var document = solution.GetDocument(documentId);
            var root = document.GetSyntaxRootAsync(CancellationToken.None).Result.WithAdditionalAnnotations(Formatter.Annotation);
            solution = solution.WithDocumentSyntaxRoot(documentId, root);

            _document = solution.GetDocument(documentId);
            _options = new CSharpSyntaxFormattingOptions()
            {
                NewLines = CSharpSyntaxFormattingOptions.Default.NewLines | NewLinePlacement.BeforeOpenBraceInTypes
            };
        }

        [Benchmark]
        public void FormatSyntaxNode()
        {
            for (int i = 0; i < _iterationCount; ++i)
            {
                var formattedDoc = Formatter.FormatAsync(_document, Formatter.Annotation, _options, CancellationToken.None).Result;
                var formattedRoot = formattedDoc.GetSyntaxRootAsync(CancellationToken.None).Result;
                _ = formattedRoot.DescendantNodesAndSelf().ToImmutableArray();
            }
        }
    }
}