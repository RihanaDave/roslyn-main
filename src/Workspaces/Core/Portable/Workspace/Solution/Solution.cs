﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Collections.Immutable;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    /// <summary>
    /// Represents a set of projects and their source code documents. 
    /// </summary>
    public partial class Solution
    {
        private readonly SolutionCompilationState _compilationState;

        // Values for all these are created on demand.
        private ImmutableHashMap<ProjectId, Project> _projectIdToProjectMap;

        private Solution(SolutionCompilationState compilationState)
        {
            _projectIdToProjectMap = ImmutableHashMap<ProjectId, Project>.Empty;
            _compilationState = compilationState;
        }

        internal Solution(
            Workspace workspace,
            SolutionInfo.SolutionAttributes solutionAttributes,
            SolutionOptionSet options,
            IReadOnlyList<AnalyzerReference> analyzerReferences)
            : this(new SolutionCompilationState(
                      new SolutionState(workspace.Kind, workspace.Services.SolutionServices, solutionAttributes, options, analyzerReferences),
                      workspace.PartialSemanticsEnabled))
        {
        }

        internal SolutionState SolutionState => CompilationState.SolutionState;

        internal SolutionCompilationState CompilationState => _compilationState;

        internal int WorkspaceVersion => this.SolutionState.WorkspaceVersion;

        internal bool PartialSemanticsEnabled => _compilationState.PartialSemanticsEnabled;

        /// <summary>
        /// Per solution services provided by the host environment.  Use this instead of <see
        /// cref="Workspace.Services"/> when possible.
        /// </summary>
        public SolutionServices Services => this.SolutionState.Services;

        internal string? WorkspaceKind => this.SolutionState.WorkspaceKind;

        internal ProjectState? GetProjectState(ProjectId projectId) => this.SolutionState.GetProjectState(projectId);

        /// <summary>
        /// The Workspace this solution is associated with.
        /// </summary>
        public Workspace Workspace
        {
            get
            {
                Contract.ThrowIfTrue(this.WorkspaceKind == CodeAnalysis.WorkspaceKind.RemoteWorkspace, "Access .Workspace off of a RemoteWorkspace Solution is not supported.");
#pragma warning disable CS0618 // Type or member is obsolete (TODO: obsolete the property)
                return this.SolutionState.Services.WorkspaceServices.Workspace;
#pragma warning restore
            }
        }

        /// <summary>
        /// The Id of the solution. Multiple solution instances may share the same Id.
        /// </summary>
        public SolutionId Id => this.SolutionState.Id;

        /// <summary>
        /// The path to the solution file or null if there is no solution file.
        /// </summary>
        public string? FilePath => this.SolutionState.FilePath;

        /// <summary>
        /// The solution version. This equates to the solution file's version.
        /// </summary>
        public VersionStamp Version => this.SolutionState.Version;

        /// <summary>
        /// A list of all the ids for all the projects contained by the solution.
        /// </summary>
        public IReadOnlyList<ProjectId> ProjectIds => this.SolutionState.ProjectIds;

        /// <summary>
        /// A list of all the projects contained by the solution.
        /// </summary>
        public IEnumerable<Project> Projects => ProjectIds.Select(id => GetProject(id)!);

        /// <summary>
        /// The version of the most recently modified project.
        /// </summary>
        public VersionStamp GetLatestProjectVersion() => this.SolutionState.GetLatestProjectVersion();

        /// <summary>
        /// True if the solution contains a project with the specified project ID.
        /// </summary>
        public bool ContainsProject([NotNullWhen(returnValue: true)] ProjectId? projectId) => this.SolutionState.ContainsProject(projectId);

        /// <summary>
        /// Gets the project in this solution with the specified project ID. 
        /// 
        /// If the id is not an id of a project that is part of this solution the method returns null.
        /// </summary>
        public Project? GetProject(ProjectId? projectId)
        {
            if (this.ContainsProject(projectId))
            {
                return ImmutableHashMapExtensions.GetOrAdd(ref _projectIdToProjectMap, projectId, s_createProjectFunction, this);
            }

            return null;
        }

        private static readonly Func<ProjectId, Solution, Project> s_createProjectFunction = CreateProject;
        private static Project CreateProject(ProjectId projectId, Solution solution)
        {
            var state = solution.SolutionState.GetProjectState(projectId);
            Contract.ThrowIfNull(state);
            return new Project(solution, state);
        }

#pragma warning disable IDE0060 // Remove unused parameter 'cancellationToken' - shipped public API
        /// <summary>
        /// Gets the <see cref="Project"/> associated with an assembly symbol.
        /// </summary>
        public Project? GetProject(IAssemblySymbol assemblySymbol,
            CancellationToken cancellationToken = default)
#pragma warning restore IDE0060 // Remove unused parameter
        {
            var projectId = SolutionCompilationState.GetProjectId(assemblySymbol);
            return GetProject(projectId);
        }

        /// <summary>
        /// Given a <paramref name="symbol"/> returns the <see cref="ProjectId"/> of the <see cref="Project"/> it came
        /// from.  Returns <see langword="null"/> if <paramref name="symbol"/> does not come from any project in this solution.
        /// </summary>
        /// <remarks>
        /// This function differs from <see cref="GetProject(IAssemblySymbol, CancellationToken)"/> in terms of how it
        /// treats <see cref="IAssemblySymbol"/>s.  Specifically, say there is the following:
        ///
        /// <c>
        /// Project-A, containing Symbol-A.<para/>
        /// Project-B, with a reference to Project-A, and usage of Symbol-A.
        /// </c>
        ///
        /// It is possible (with retargeting, and other complex cases) that Symbol-A from Project-B will be a different
        /// symbol than Symbol-A from Project-A.  However, <see cref="GetProject(IAssemblySymbol, CancellationToken)"/>
        /// will always try to return Project-A for either of the Symbol-A's, as it prefers to return the original
        /// Source-Project of the original definition, not the project that actually produced the symbol.  For many
        /// features this is an acceptable abstraction.  However, for some cases (Find-References in particular) it is
        /// necessary to resolve symbols back to the actual project/compilation that produced them for correctness.
        /// </remarks>
        internal ProjectId? GetOriginatingProjectId(ISymbol symbol)
            => _compilationState.GetOriginatingProjectId(symbol);

        /// <inheritdoc cref="GetOriginatingProjectId"/>
        internal Project? GetOriginatingProject(ISymbol symbol)
            => GetProject(GetOriginatingProjectId(symbol));

        /// <summary>
        /// True if the solution contains the document in one of its projects
        /// </summary>
        public bool ContainsDocument([NotNullWhen(returnValue: true)] DocumentId? documentId) => this.SolutionState.ContainsDocument(documentId);

        /// <summary>
        /// True if the solution contains the additional document in one of its projects
        /// </summary>
        public bool ContainsAdditionalDocument([NotNullWhen(returnValue: true)] DocumentId? documentId) => this.SolutionState.ContainsAdditionalDocument(documentId);

        /// <summary>
        /// True if the solution contains the analyzer config document in one of its projects
        /// </summary>
        public bool ContainsAnalyzerConfigDocument([NotNullWhen(returnValue: true)] DocumentId? documentId) => this.SolutionState.ContainsAnalyzerConfigDocument(documentId);

        /// <summary>
        /// Gets the documentId in this solution with the specified syntax tree.
        /// </summary>
        public DocumentId? GetDocumentId(SyntaxTree? syntaxTree) => GetDocumentId(syntaxTree, projectId: null);

        /// <summary>
        /// Gets the documentId in this solution with the specified syntax tree.
        /// </summary>
        public DocumentId? GetDocumentId(SyntaxTree? syntaxTree, ProjectId? projectId)
            => _compilationState.GetDocumentState(syntaxTree, projectId)?.Id;

        /// <summary>
        /// Gets the document in this solution with the specified document ID.
        /// </summary>
        public Document? GetDocument(DocumentId? documentId)
            => GetProject(documentId?.ProjectId)?.GetDocument(documentId!);

        /// <summary>
        /// Gets a document or a source generated document in this solution with the specified document ID.
        /// </summary>
        internal ValueTask<Document?> GetDocumentAsync(DocumentId? documentId, bool includeSourceGenerated = false, CancellationToken cancellationToken = default)
        {
            var project = GetProject(documentId?.ProjectId);
            if (project == null)
            {
                return default;
            }

            Contract.ThrowIfNull(documentId);
            return project.GetDocumentAsync(documentId, includeSourceGenerated, cancellationToken);
        }

        /// <summary>
        /// Gets a document, additional document, analyzer config document or a source generated document in this solution with the specified document ID.
        /// </summary>
        internal ValueTask<TextDocument?> GetTextDocumentAsync(DocumentId? documentId, CancellationToken cancellationToken = default)
        {
            var project = GetProject(documentId?.ProjectId);
            if (project == null)
            {
                return default;
            }

            Contract.ThrowIfNull(documentId);
            return project.GetTextDocumentAsync(documentId, cancellationToken);
        }

        /// <summary>
        /// Gets the additional document in this solution with the specified document ID.
        /// </summary>
        public TextDocument? GetAdditionalDocument(DocumentId? documentId)
        {
            if (this.ContainsAdditionalDocument(documentId))
            {
                return this.GetProject(documentId.ProjectId)!.GetAdditionalDocument(documentId);
            }

            return null;
        }

        /// <summary>
        /// Gets the analyzer config document in this solution with the specified document ID.
        /// </summary>
        public AnalyzerConfigDocument? GetAnalyzerConfigDocument(DocumentId? documentId)
        {
            if (this.ContainsAnalyzerConfigDocument(documentId))
            {
                return this.GetProject(documentId.ProjectId)!.GetAnalyzerConfigDocument(documentId);
            }

            return null;
        }

        public ValueTask<SourceGeneratedDocument?> GetSourceGeneratedDocumentAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            var project = GetProject(documentId.ProjectId);

            if (project == null)
            {
                return new(result: null);
            }
            else
            {
                return project.GetSourceGeneratedDocumentAsync(documentId, cancellationToken);
            }
        }

        /// <summary>
        /// Gets the document in this solution with the specified syntax tree.
        /// </summary>
        public Document? GetDocument(SyntaxTree? syntaxTree)
            => this.GetDocument(syntaxTree, projectId: null);

        internal Document? GetDocument(SyntaxTree? syntaxTree, ProjectId? projectId)
        {
            if (syntaxTree != null)
            {
                var documentState = _compilationState.GetDocumentState(syntaxTree, projectId);

                if (documentState is SourceGeneratedDocumentState)
                {
                    // We have the underlying state, but we need to get the wrapper SourceGeneratedDocument object. The wrapping is maintained by
                    // the Project object, so we'll now fetch the project and ask it to get the SourceGeneratedDocument wrapper. Under the covers this
                    // implicity may call to fetch the SourceGeneratedDocumentState a second time but that's not expensive.
                    var generatedDocument = this.GetRequiredProject(documentState.Id.ProjectId).TryGetSourceGeneratedDocumentForAlreadyGeneratedId(documentState.Id);
                    Contract.ThrowIfNull(generatedDocument, "The call to GetDocumentState found a SourceGeneratedDocumentState, so we should have found it now.");
                    return generatedDocument;
                }
                else if (documentState is DocumentState)
                {
                    return GetDocument(documentState.Id)!;
                }
            }

            return null;
        }

        /// <summary>
        /// Creates a new solution instance that includes a project with the specified language and names.
        /// Returns the new project.
        /// </summary>
        public Project AddProject(string name, string assemblyName, string language)
        {
            var id = ProjectId.CreateNewId(debugName: name);
            return this.AddProject(id, name, assemblyName, language).GetProject(id)!;
        }

        /// <summary>
        /// Creates a new solution instance that includes a project with the specified language and names.
        /// </summary>
        public Solution AddProject(ProjectId projectId, string name, string assemblyName, string language)
            => this.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), name, assemblyName, language));

        /// <summary>
        /// Create a new solution instance that includes a project with the specified project information.
        /// </summary>
        public Solution AddProject(ProjectInfo projectInfo)
        {
            var newCompilationState = _compilationState.AddProject(projectInfo);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance without the project specified.
        /// </summary>
        public Solution RemoveProject(ProjectId projectId)
        {
            var newCompilationState = _compilationState.RemoveProject(projectId);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the project specified updated to have the new
        /// assembly name.
        /// </summary>
        public Solution WithProjectAssemblyName(ProjectId projectId, string assemblyName)
        {
            CheckContainsProject(projectId);

            if (assemblyName == null)
            {
                throw new ArgumentNullException(nameof(assemblyName));
            }

            var newCompilationState = _compilationState.WithProjectAssemblyName(projectId, assemblyName);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the project specified updated to have the output file path.
        /// </summary>
        public Solution WithProjectOutputFilePath(ProjectId projectId, string? outputFilePath)
        {
            CheckContainsProject(projectId);

            var newCompilationState = _compilationState.WithProjectOutputFilePath(projectId, outputFilePath);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the project specified updated to have the reference assembly output file path.
        /// </summary>
        public Solution WithProjectOutputRefFilePath(ProjectId projectId, string? outputRefFilePath)
        {
            CheckContainsProject(projectId);

            var newCompilationState = _compilationState.WithProjectOutputRefFilePath(projectId, outputRefFilePath);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the project specified updated to have the compiler output file path.
        /// </summary>
        public Solution WithProjectCompilationOutputInfo(ProjectId projectId, in CompilationOutputInfo info)
        {
            CheckContainsProject(projectId);

            var newCompilationState = _compilationState.WithProjectCompilationOutputInfo(projectId, info);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the project specified updated to have the default namespace.
        /// </summary>
        public Solution WithProjectDefaultNamespace(ProjectId projectId, string? defaultNamespace)
        {
            CheckContainsProject(projectId);

            var newCompilationState = _compilationState.WithProjectDefaultNamespace(projectId, defaultNamespace);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the project specified updated to have the specified attributes.
        /// </summary>
        internal Solution WithProjectChecksumAlgorithm(ProjectId projectId, SourceHashAlgorithm checksumAlgorithm)
        {
            CheckContainsProject(projectId);

            var newCompilationState = _compilationState.WithProjectChecksumAlgorithm(projectId, checksumAlgorithm);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the project specified updated to have the name.
        /// </summary>
        public Solution WithProjectName(ProjectId projectId, string name)
        {
            CheckContainsProject(projectId);

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            var newCompilationState = _compilationState.WithProjectName(projectId, name);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the project specified updated to have the project file path.
        /// </summary>
        public Solution WithProjectFilePath(ProjectId projectId, string? filePath)
        {
            CheckContainsProject(projectId);

            var newCompilationState = _compilationState.WithProjectFilePath(projectId, filePath);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to have
        /// the specified compilation options.
        /// </summary>
        public Solution WithProjectCompilationOptions(ProjectId projectId, CompilationOptions options)
        {
            CheckContainsProject(projectId);

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var newCompilationState = _compilationState.WithProjectCompilationOptions(projectId, options);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to have
        /// the specified parse options.
        /// </summary>
        public Solution WithProjectParseOptions(ProjectId projectId, ParseOptions options)
        {
            CheckContainsProject(projectId);

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var newCompilationState = _compilationState.WithProjectParseOptions(projectId, options);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to have
        /// the specified hasAllInformation.
        /// </summary>
        // TODO: https://github.com/dotnet/roslyn/issues/42449 make it public
        internal Solution WithHasAllInformation(ProjectId projectId, bool hasAllInformation)
        {
            CheckContainsProject(projectId);

            var newCompilationState = _compilationState.WithHasAllInformation(projectId, hasAllInformation);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to have
        /// the specified runAnalyzers.
        /// </summary>
        // TODO: https://github.com/dotnet/roslyn/issues/42449 make it public
        internal Solution WithRunAnalyzers(ProjectId projectId, bool runAnalyzers)
        {
            CheckContainsProject(projectId);

            var newCompilationState = _compilationState.WithRunAnalyzers(projectId, runAnalyzers);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the project documents in the order by the specified document ids.
        /// The specified document ids must be the same as what is already in the project; no adding or removing is allowed.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="documentIds"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The solution does not contain <paramref name="projectId"/>.</exception>
        /// <exception cref="ArgumentException">The number of documents specified in <paramref name="documentIds"/> is not equal to the number of documents in project <paramref name="projectId"/>.</exception>
        /// <exception cref="InvalidOperationException">Document specified in <paramref name="documentIds"/> does not exist in project <paramref name="projectId"/>.</exception>
        public Solution WithProjectDocumentsOrder(ProjectId projectId, ImmutableList<DocumentId> documentIds)
        {
            CheckContainsProject(projectId);

            if (documentIds == null)
            {
                throw new ArgumentNullException(nameof(documentIds));
            }

            var newCompilationState = _compilationState.WithProjectDocumentsOrder(projectId, documentIds);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to include
        /// the specified project reference.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="projectReference"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The solution does not contain <paramref name="projectId"/>.</exception>
        /// <exception cref="InvalidOperationException">The project already references the target project.</exception>
        public Solution AddProjectReference(ProjectId projectId, ProjectReference projectReference)
        {
            return AddProjectReferences(projectId,
                SpecializedCollections.SingletonEnumerable(
                    projectReference ?? throw new ArgumentNullException(nameof(projectReference))));
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to include
        /// the specified project references.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="projectReferences"/> contains <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="projectReferences"/> contains duplicate items.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        /// <exception cref="InvalidOperationException">The project already references the target project.</exception>
        /// <exception cref="InvalidOperationException">Adding the project reference would create a circular dependency.</exception>
        public Solution AddProjectReferences(ProjectId projectId, IEnumerable<ProjectReference> projectReferences)
        {
            CheckContainsProject(projectId);

            // avoid enumerating multiple times:
            var collection = projectReferences?.ToCollection();

            PublicContract.RequireUniqueNonNullItems(collection, nameof(projectReferences));

            foreach (var projectReference in collection)
            {
                if (this.SolutionState.ContainsProjectReference(projectId, projectReference))
                {
                    throw new InvalidOperationException(WorkspacesResources.The_project_already_references_the_target_project);
                }
            }

            CheckCircularProjectReferences(projectId, collection);
            CheckSubmissionProjectReferences(projectId, collection, ignoreExistingReferences: false);

            var newCompilationState = _compilationState.AddProjectReferences(projectId, collection);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to no longer
        /// include the specified project reference.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="projectReference"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The solution does not contain <paramref name="projectId"/>.</exception>
        public Solution RemoveProjectReference(ProjectId projectId, ProjectReference projectReference)
        {
            if (projectReference == null)
                throw new ArgumentNullException(nameof(projectReference));

            CheckContainsProject(projectId);

            var oldProject = GetRequiredProjectState(projectId);
            if (!oldProject.ProjectReferences.Contains(projectReference))
                throw new ArgumentException(WorkspacesResources.Project_does_not_contain_specified_reference, nameof(projectReference));

            var newCompilationState = _compilationState.RemoveProjectReference(projectId, projectReference);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to contain
        /// the specified list of project references.
        /// </summary>
        /// <param name="projectId">Id of the project whose references to replace with <paramref name="projectReferences"/>.</param>
        /// <param name="projectReferences">New project references.</param>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="projectReferences"/> contains <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="projectReferences"/> contains duplicate items.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        public Solution WithProjectReferences(ProjectId projectId, IEnumerable<ProjectReference>? projectReferences)
        {
            CheckContainsProject(projectId);

            // avoid enumerating multiple times:
            var collection = PublicContract.ToBoxedImmutableArrayWithDistinctNonNullItems(projectReferences, nameof(projectReferences));

            CheckCircularProjectReferences(projectId, collection);
            CheckSubmissionProjectReferences(projectId, collection, ignoreExistingReferences: true);

            var newCompilationState = _compilationState.WithProjectReferences(projectId, collection);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to include the 
        /// specified metadata reference.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="metadataReference"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        /// <exception cref="InvalidOperationException">The project already contains the specified reference.</exception>
        public Solution AddMetadataReference(ProjectId projectId, MetadataReference metadataReference)
        {
            return AddMetadataReferences(projectId,
                SpecializedCollections.SingletonEnumerable(
                    metadataReference ?? throw new ArgumentNullException(nameof(metadataReference))));
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to include the
        /// specified metadata references.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="metadataReferences"/> contains <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="metadataReferences"/> contains duplicate items.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        /// <exception cref="InvalidOperationException">The project already contains the specified reference.</exception>
        public Solution AddMetadataReferences(ProjectId projectId, IEnumerable<MetadataReference> metadataReferences)
        {
            CheckContainsProject(projectId);

            // avoid enumerating multiple times:
            var collection = metadataReferences?.ToCollection();

            PublicContract.RequireUniqueNonNullItems(collection, nameof(metadataReferences));
            foreach (var metadataReference in collection)
            {
                if (this.SolutionState.ContainsMetadataReference(projectId, metadataReference))
                {
                    throw new InvalidOperationException(WorkspacesResources.The_project_already_contains_the_specified_reference);
                }
            }

            var newCompilationState = _compilationState.AddMetadataReferences(projectId, collection);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to no longer include
        /// the specified metadata reference.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="metadataReference"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        /// <exception cref="InvalidOperationException">The project does not contain the specified reference.</exception>
        public Solution RemoveMetadataReference(ProjectId projectId, MetadataReference metadataReference)
        {
            CheckContainsProject(projectId);

            if (metadataReference == null)
                throw new ArgumentNullException(nameof(metadataReference));

            var oldProject = GetRequiredProjectState(projectId);
            if (!oldProject.MetadataReferences.Contains(metadataReference))
                throw new InvalidOperationException(WorkspacesResources.Project_does_not_contain_specified_reference);

            var newCompilationState = _compilationState.RemoveMetadataReference(projectId, metadataReference);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to include only the
        /// specified metadata references.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="metadataReferences"/> contains <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="metadataReferences"/> contains duplicate items.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        public Solution WithProjectMetadataReferences(ProjectId projectId, IEnumerable<MetadataReference> metadataReferences)
        {
            CheckContainsProject(projectId);

            var collection = PublicContract.ToBoxedImmutableArrayWithDistinctNonNullItems(metadataReferences, nameof(metadataReferences));

            var newCompilationState = _compilationState.WithProjectMetadataReferences(projectId, collection);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to include the 
        /// specified analyzer reference.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="analyzerReference"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        public Solution AddAnalyzerReference(ProjectId projectId, AnalyzerReference analyzerReference)
        {
            return AddAnalyzerReferences(projectId,
                SpecializedCollections.SingletonEnumerable(
                    analyzerReference ?? throw new ArgumentNullException(nameof(analyzerReference))));
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to include the
        /// specified analyzer references.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="analyzerReferences"/> contains <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="analyzerReferences"/> contains duplicate items.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        /// <exception cref="InvalidOperationException">The project already contains the specified reference.</exception>
        public Solution AddAnalyzerReferences(ProjectId projectId, IEnumerable<AnalyzerReference> analyzerReferences)
        {
            CheckContainsProject(projectId);

            if (analyzerReferences is null)
            {
                throw new ArgumentNullException(nameof(analyzerReferences));
            }

            var collection = analyzerReferences.ToImmutableArray();

            PublicContract.RequireUniqueNonNullItems(collection, nameof(analyzerReferences));

            foreach (var analyzerReference in collection)
            {
                if (this.SolutionState.ContainsAnalyzerReference(projectId, analyzerReference))
                {
                    throw new InvalidOperationException(WorkspacesResources.The_project_already_contains_the_specified_reference);
                }
            }

            var newCompilationState = _compilationState.AddAnalyzerReferences(this.SolutionState.AddAnalyzerReferences(projectId, collection), collection);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to no longer include
        /// the specified analyzer reference.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="analyzerReference"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        /// <exception cref="InvalidOperationException">The project does not contain the specified reference.</exception>
        public Solution RemoveAnalyzerReference(ProjectId projectId, AnalyzerReference analyzerReference)
        {
            CheckContainsProject(projectId);

            if (analyzerReference == null)
                throw new ArgumentNullException(nameof(analyzerReference));

            var oldProject = GetRequiredProjectState(projectId);
            if (!oldProject.AnalyzerReferences.Contains(analyzerReference))
                throw new InvalidOperationException(WorkspacesResources.Project_does_not_contain_specified_reference);

            var newCompilationState = _compilationState.RemoveAnalyzerReference(projectId, analyzerReference);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to include only the
        /// specified analyzer references.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="analyzerReferences"/> contains <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="analyzerReferences"/> contains duplicate items.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain <paramref name="projectId"/>.</exception>
        public Solution WithProjectAnalyzerReferences(ProjectId projectId, IEnumerable<AnalyzerReference> analyzerReferences)
        {
            CheckContainsProject(projectId);

            var collection = PublicContract.ToBoxedImmutableArrayWithDistinctNonNullItems(analyzerReferences, nameof(analyzerReferences));

            var newCompilationState = _compilationState.WithProjectAnalyzerReferences(projectId, collection);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance updated to include the specified analyzer reference.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="analyzerReference"/> is <see langword="null"/>.</exception>
        public Solution AddAnalyzerReference(AnalyzerReference analyzerReference)
        {
            return AddAnalyzerReferences(
                SpecializedCollections.SingletonEnumerable(
                    analyzerReference ?? throw new ArgumentNullException(nameof(analyzerReference))));
        }

        /// <summary>
        /// Create a new solution instance updated to include the specified analyzer references.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="analyzerReferences"/> contains <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="analyzerReferences"/> contains duplicate items.</exception>
        /// <exception cref="InvalidOperationException">The solution already contains the specified reference.</exception>
        public Solution AddAnalyzerReferences(IEnumerable<AnalyzerReference> analyzerReferences)
        {
            // avoid enumerating multiple times:
            var collection = analyzerReferences?.ToCollection();

            PublicContract.RequireUniqueNonNullItems(collection, nameof(analyzerReferences));

            foreach (var analyzerReference in collection)
            {
                if (this.SolutionState.AnalyzerReferences.Contains(analyzerReference))
                {
                    throw new InvalidOperationException(WorkspacesResources.The_solution_already_contains_the_specified_reference);
                }
            }

            var newCompilationState = _compilationState.AddAnalyzerReferences(collection);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Create a new solution instance with the project specified updated to no longer include
        /// the specified analyzer reference.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="analyzerReference"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The solution does not contain the specified reference.</exception>
        public Solution RemoveAnalyzerReference(AnalyzerReference analyzerReference)
        {
            if (analyzerReference == null)
                throw new ArgumentNullException(nameof(analyzerReference));

            if (!this.SolutionState.AnalyzerReferences.Contains(analyzerReference))
                throw new InvalidOperationException(WorkspacesResources.Solution_does_not_contain_specified_reference);

            var newCompilationState = _compilationState.RemoveAnalyzerReference(analyzerReference);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the specified analyzer references.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="analyzerReferences"/> contains <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="analyzerReferences"/> contains duplicate items.</exception>
        public Solution WithAnalyzerReferences(IEnumerable<AnalyzerReference> analyzerReferences)
        {
            var collection = PublicContract.ToBoxedImmutableArrayWithDistinctNonNullItems(analyzerReferences, nameof(analyzerReferences));

            var newCompilationState = _compilationState.WithAnalyzerReferences(collection);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        private static SourceCodeKind GetSourceCodeKind(ProjectState project)
            => project.ParseOptions != null ? project.ParseOptions.Kind : SourceCodeKind.Regular;

        /// <summary>
        /// Creates a new solution instance with the corresponding project updated to include a new
        /// document instance defined by its name and text.
        /// </summary>
        public Solution AddDocument(DocumentId documentId, string name, string text, IEnumerable<string>? folders = null, string? filePath = null)
        {
            if (documentId == null)
                throw new ArgumentNullException(nameof(documentId));

            if (name == null)
                throw new ArgumentNullException(nameof(name));

            var project = GetRequiredProjectState(documentId.ProjectId);
            var sourceText = SourceText.From(text, encoding: null, checksumAlgorithm: project.ChecksumAlgorithm);

            return AddDocumentImpl(project, documentId, name, sourceText, PublicContract.ToBoxedImmutableArrayWithNonNullItems(folders, nameof(folders)), filePath, isGenerated: false);
        }

        /// <summary>
        /// Creates a new solution instance with the corresponding project updated to include a new
        /// document instance defined by its name and text.
        /// </summary>
        public Solution AddDocument(DocumentId documentId, string name, SourceText text, IEnumerable<string>? folders = null, string? filePath = null, bool isGenerated = false)
        {
            if (documentId == null)
                throw new ArgumentNullException(nameof(documentId));

            if (name == null)
                throw new ArgumentNullException(nameof(name));

            if (text == null)
                throw new ArgumentNullException(nameof(text));

            var project = GetRequiredProjectState(documentId.ProjectId);
            return AddDocumentImpl(project, documentId, name, text, PublicContract.ToBoxedImmutableArrayWithNonNullItems(folders, nameof(folders)), filePath, isGenerated);
        }

        /// <summary>
        /// Creates a new solution instance with the corresponding project updated to include a new
        /// document instance defined by its name and root <see cref="SyntaxNode"/>.
        /// </summary>
        public Solution AddDocument(DocumentId documentId, string name, SyntaxNode syntaxRoot, IEnumerable<string>? folders = null, string? filePath = null, bool isGenerated = false, PreservationMode preservationMode = PreservationMode.PreserveValue)
        {
            if (documentId == null)
                throw new ArgumentNullException(nameof(documentId));

            if (name == null)
                throw new ArgumentNullException(nameof(name));

            if (syntaxRoot == null)
                throw new ArgumentNullException(nameof(syntaxRoot));

            var project = GetRequiredProjectState(documentId.ProjectId);
            var sourceText = SourceText.From(string.Empty, encoding: null, project.ChecksumAlgorithm);

            return AddDocumentImpl(project, documentId, name, sourceText, PublicContract.ToBoxedImmutableArrayWithNonNullItems(folders, nameof(folders)), filePath, isGenerated).
                WithDocumentSyntaxRoot(documentId, syntaxRoot, preservationMode);
        }

        private Solution AddDocumentImpl(ProjectState project, DocumentId documentId, string name, SourceText text, IReadOnlyList<string>? folders, string? filePath, bool isGenerated)
            => AddDocument(DocumentInfo.Create(
                documentId,
                name: name,
                folders: folders,
                sourceCodeKind: GetSourceCodeKind(project),
                loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), name)),
                filePath: filePath,
                isGenerated: isGenerated));

        /// <summary>
        /// Creates a new solution instance with the project updated to include a new document with
        /// the arguments specified.
        /// </summary>
        public Solution AddDocument(DocumentId documentId, string name, TextLoader loader, IEnumerable<string>? folders = null)
        {
            if (documentId == null)
                throw new ArgumentNullException(nameof(documentId));

            if (name == null)
                throw new ArgumentNullException(nameof(name));

            if (loader == null)
                throw new ArgumentNullException(nameof(loader));

            var project = GetRequiredProjectState(documentId.ProjectId);

            return AddDocument(DocumentInfo.Create(
                documentId,
                name,
                folders,
                GetSourceCodeKind(project),
                loader));
        }

        /// <summary>
        /// Create a new solution instance with the corresponding project updated to include a new 
        /// document instanced defined by the document info.
        /// </summary>
        public Solution AddDocument(DocumentInfo documentInfo)
            => AddDocuments(ImmutableArray.Create(documentInfo));

        /// <summary>
        /// Create a new <see cref="Solution"/> instance with the corresponding <see cref="Project"/>s updated to include
        /// the documents specified by <paramref name="documentInfos"/>.
        /// </summary>
        /// <returns>A new <see cref="Solution"/> with the documents added.</returns>
        public Solution AddDocuments(ImmutableArray<DocumentInfo> documentInfos)
        {
            var newCompilationState = _compilationState.AddDocuments(documentInfos);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the corresponding project updated to include a new
        /// additional document instance defined by its name and text.
        /// </summary>
        public Solution AddAdditionalDocument(DocumentId documentId, string name, string text, IEnumerable<string>? folders = null, string? filePath = null)
            => this.AddAdditionalDocument(documentId, name, SourceText.From(text), folders, filePath);

        /// <summary>
        /// Creates a new solution instance with the corresponding project updated to include a new
        /// additional document instance defined by its name and text.
        /// </summary>
        public Solution AddAdditionalDocument(DocumentId documentId, string name, SourceText text, IEnumerable<string>? folders = null, string? filePath = null)
        {
            if (documentId == null)
            {
                throw new ArgumentNullException(nameof(documentId));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var info = CreateDocumentInfo(documentId, name, text, folders, filePath);
            return this.AddAdditionalDocument(info);
        }

        public Solution AddAdditionalDocument(DocumentInfo documentInfo)
            => AddAdditionalDocuments(ImmutableArray.Create(documentInfo));

        public Solution AddAdditionalDocuments(ImmutableArray<DocumentInfo> documentInfos)
        {
            var newCompilationState = _compilationState.AddAdditionalDocuments(documentInfos);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the corresponding project updated to include a new
        /// analyzer config document instance defined by its name and text.
        /// </summary>
        public Solution AddAnalyzerConfigDocument(DocumentId documentId, string name, SourceText text, IEnumerable<string>? folders = null, string? filePath = null)
        {
            if (documentId == null)
            {
                throw new ArgumentNullException(nameof(documentId));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            // TODO: should we validate file path?
            // https://github.com/dotnet/roslyn/issues/41940

            var info = CreateDocumentInfo(documentId, name, text, folders, filePath);
            return this.AddAnalyzerConfigDocuments(ImmutableArray.Create(info));
        }

        private DocumentInfo CreateDocumentInfo(DocumentId documentId, string name, SourceText text, IEnumerable<string>? folders, string? filePath)
        {
            var project = GetRequiredProjectState(documentId.ProjectId);
            var version = VersionStamp.Create();
            var loader = TextLoader.From(TextAndVersion.Create(text, version, name));

            return DocumentInfo.Create(
                documentId,
                name: name,
                folders: folders,
                sourceCodeKind: GetSourceCodeKind(project),
                loader: loader,
                filePath: filePath);
        }

        private ProjectState GetRequiredProjectState(ProjectId projectId)
            => this.SolutionState.GetProjectState(projectId) ?? throw new InvalidOperationException(string.Format(WorkspacesResources._0_is_not_part_of_the_workspace, projectId));

        /// <summary>
        /// Creates a new Solution instance that contains a new compiler configuration document like a .editorconfig file.
        /// </summary>
        public Solution AddAnalyzerConfigDocuments(ImmutableArray<DocumentInfo> documentInfos)
        {
            var newCompilationState = _compilationState.AddAnalyzerConfigDocuments(documentInfos);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance that no longer includes the specified document.
        /// </summary>
        public Solution RemoveDocument(DocumentId documentId)
        {
            CheckContainsDocument(documentId);
            return RemoveDocumentsImpl(ImmutableArray.Create(documentId));
        }

        /// <summary>
        /// Creates a new solution instance that no longer includes the specified documents.
        /// </summary>
        public Solution RemoveDocuments(ImmutableArray<DocumentId> documentIds)
        {
            CheckContainsDocuments(documentIds);
            return RemoveDocumentsImpl(documentIds);
        }

        private Solution RemoveDocumentsImpl(ImmutableArray<DocumentId> documentIds)
        {
            var newCompilationState = _compilationState.RemoveDocuments(documentIds);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance that no longer includes the specified additional document.
        /// </summary>
        public Solution RemoveAdditionalDocument(DocumentId documentId)
        {
            CheckContainsAdditionalDocument(documentId);
            return RemoveAdditionalDocumentsImpl(ImmutableArray.Create(documentId));
        }

        /// <summary>
        /// Creates a new solution instance that no longer includes the specified additional documents.
        /// </summary>
        public Solution RemoveAdditionalDocuments(ImmutableArray<DocumentId> documentIds)
        {
            CheckContainsAdditionalDocuments(documentIds);
            return RemoveAdditionalDocumentsImpl(documentIds);
        }

        private Solution RemoveAdditionalDocumentsImpl(ImmutableArray<DocumentId> documentIds)
        {
            var newCompilationState = _compilationState.RemoveAdditionalDocuments(documentIds);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance that no longer includes the specified <see cref="AnalyzerConfigDocument"/>.
        /// </summary>
        public Solution RemoveAnalyzerConfigDocument(DocumentId documentId)
        {
            CheckContainsAnalyzerConfigDocument(documentId);
            return RemoveAnalyzerConfigDocumentsImpl(ImmutableArray.Create(documentId));
        }

        /// <summary>
        /// Creates a new solution instance that no longer includes the specified <see cref="AnalyzerConfigDocument"/>s.
        /// </summary>
        public Solution RemoveAnalyzerConfigDocuments(ImmutableArray<DocumentId> documentIds)
        {
            CheckContainsAnalyzerConfigDocuments(documentIds);
            return RemoveAnalyzerConfigDocumentsImpl(documentIds);
        }

        private Solution RemoveAnalyzerConfigDocumentsImpl(ImmutableArray<DocumentId> documentIds)
        {
            var newCompilationState = _compilationState.RemoveAnalyzerConfigDocuments(documentIds);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the document specified updated to have the new name.
        /// </summary>
        public Solution WithDocumentName(DocumentId documentId, string name)
        {
            CheckContainsDocument(documentId);

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            var newCompilationState = _compilationState.WithDocumentName(documentId, name);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the document specified updated to be contained in
        /// the sequence of logical folders.
        /// </summary>
        public Solution WithDocumentFolders(DocumentId documentId, IEnumerable<string>? folders)
        {
            CheckContainsDocument(documentId);

            var collection = PublicContract.ToBoxedImmutableArrayWithNonNullItems(folders, nameof(folders));

            var newCompilationState = _compilationState.WithDocumentFolders(documentId, collection);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the document specified updated to have the specified file path.
        /// </summary>
        public Solution WithDocumentFilePath(DocumentId documentId, string filePath)
        {
            CheckContainsDocument(documentId);

            // TODO (https://github.com/dotnet/roslyn/issues/37125): 
            // We *do* support null file paths. Why can't you switch a document back to null?
            // See DocumentState.GetSyntaxTreeFilePath
            if (filePath == null)
            {
                throw new ArgumentNullException(nameof(filePath));
            }

            var newCompilationState = _compilationState.WithDocumentFilePath(documentId, filePath);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the document specified updated to have the text
        /// specified.
        /// </summary>
        public Solution WithDocumentText(DocumentId documentId, SourceText text, PreservationMode mode = PreservationMode.PreserveValue)
        {
            CheckContainsDocument(documentId);

            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.WithDocumentText(documentId, text, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the additional document specified updated to have the text
        /// specified.
        /// </summary>
        public Solution WithAdditionalDocumentText(DocumentId documentId, SourceText text, PreservationMode mode = PreservationMode.PreserveValue)
        {
            CheckContainsAdditionalDocument(documentId);

            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.WithAdditionalDocumentText(documentId, text, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the analyzer config document specified updated to have the text
        /// supplied by the text loader.
        /// </summary>
        public Solution WithAnalyzerConfigDocumentText(DocumentId documentId, SourceText text, PreservationMode mode = PreservationMode.PreserveValue)
        {
            CheckContainsAnalyzerConfigDocument(documentId);

            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.WithAnalyzerConfigDocumentText(documentId, text, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the document specified updated to have the text
        /// and version specified.
        /// </summary>
        public Solution WithDocumentText(DocumentId documentId, TextAndVersion textAndVersion, PreservationMode mode = PreservationMode.PreserveValue)
        {
            CheckContainsDocument(documentId);

            if (textAndVersion == null)
            {
                throw new ArgumentNullException(nameof(textAndVersion));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.WithDocumentText(documentId, textAndVersion, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the additional document specified updated to have the text
        /// and version specified.
        /// </summary>
        public Solution WithAdditionalDocumentText(DocumentId documentId, TextAndVersion textAndVersion, PreservationMode mode = PreservationMode.PreserveValue)
        {
            CheckContainsAdditionalDocument(documentId);

            if (textAndVersion == null)
            {
                throw new ArgumentNullException(nameof(textAndVersion));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.WithAdditionalDocumentText(documentId, textAndVersion, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the analyzer config document specified updated to have the text
        /// and version specified.
        /// </summary>
        public Solution WithAnalyzerConfigDocumentText(DocumentId documentId, TextAndVersion textAndVersion, PreservationMode mode = PreservationMode.PreserveValue)
        {
            CheckContainsAnalyzerConfigDocument(documentId);

            if (textAndVersion == null)
            {
                throw new ArgumentNullException(nameof(textAndVersion));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.WithAnalyzerConfigDocumentText(documentId, textAndVersion, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the document specified updated to have a syntax tree
        /// rooted by the specified syntax node.
        /// </summary>
        public Solution WithDocumentSyntaxRoot(DocumentId documentId, SyntaxNode root, PreservationMode mode = PreservationMode.PreserveValue)
        {
            CheckContainsDocument(documentId);

            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.WithDocumentSyntaxRoot(documentId, root, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        internal Solution WithDocumentContentsFrom(DocumentId documentId, DocumentState documentState)
        {
            var newCompilationState = _compilationState.WithDocumentContentsFrom(documentId, documentState);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the document specified updated to have the source
        /// code kind specified.
        /// </summary>
        public Solution WithDocumentSourceCodeKind(DocumentId documentId, SourceCodeKind sourceCodeKind)
        {
            CheckContainsDocument(documentId);

#pragma warning disable CS0618 // Interactive is obsolete but this method accepts it for backward compatibility.
            if (sourceCodeKind == SourceCodeKind.Interactive)
            {
                sourceCodeKind = SourceCodeKind.Script;
            }
#pragma warning restore CS0618

            if (!sourceCodeKind.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(sourceCodeKind));
            }

            var newCompilationState = _compilationState.WithDocumentSourceCodeKind(documentId, sourceCodeKind);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the document specified updated to have the text
        /// supplied by the text loader.
        /// </summary>
        public Solution WithDocumentTextLoader(DocumentId documentId, TextLoader loader, PreservationMode mode)
        {
            CheckContainsDocument(documentId);

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.UpdateDocumentTextLoader(documentId, loader, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the additional document specified updated to have the text
        /// supplied by the text loader.
        /// </summary>
        public Solution WithAdditionalDocumentTextLoader(DocumentId documentId, TextLoader loader, PreservationMode mode)
        {
            CheckContainsAdditionalDocument(documentId);

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.UpdateAdditionalDocumentTextLoader(documentId, loader, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a new solution instance with the analyzer config document specified updated to have the text
        /// supplied by the text loader.
        /// </summary>
        public Solution WithAnalyzerConfigDocumentTextLoader(DocumentId documentId, TextLoader loader, PreservationMode mode)
        {
            CheckContainsAnalyzerConfigDocument(documentId);

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.UpdateAnalyzerConfigDocumentTextLoader(documentId, loader, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Creates a branch of the solution that has its compilations frozen in whatever state they are in at the time, assuming a background compiler is
        /// busy building this compilations.
        /// 
        /// A compilation for the project containing the specified document id will be guaranteed to exist with at least the syntax tree for the document.
        /// 
        /// This not intended to be the public API, use Document.WithFrozenPartialSemantics() instead.
        /// </summary>
        internal Solution WithFrozenPartialCompilationIncludingSpecificDocument(DocumentId documentId, CancellationToken cancellationToken)
        {
            var newCompilationState = _compilationState.WithFrozenPartialCompilationIncludingSpecificDocument(documentId, cancellationToken);
            return new Solution(newCompilationState);
        }

        internal async Task<Solution> WithMergedLinkedFileChangesAsync(
            Solution oldSolution,
            SolutionChanges? solutionChanges = null,
            IMergeConflictHandler? mergeConflictHandler = null,
            CancellationToken cancellationToken = default)
        {
            // we only log sessioninfo for actual changes committed to workspace which should exclude ones from preview
            var session = new LinkedFileDiffMergingSession(oldSolution, this, solutionChanges ?? this.GetChanges(oldSolution));

            return (await session.MergeDiffsAsync(mergeConflictHandler, cancellationToken).ConfigureAwait(false)).MergedSolution;
        }

        internal ImmutableArray<DocumentId> GetRelatedDocumentIds(DocumentId documentId)
        {
            return this.SolutionState.GetRelatedDocumentIds(documentId);
        }

        internal Solution WithNewWorkspace(string? workspaceKind, int workspaceVersion, SolutionServices services)
        {
            var newCompilationState = _compilationState.WithNewWorkspace(workspaceKind, workspaceVersion, services);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Formerly, returned a copy of the solution isolated from the original so that they do not share computed state. It now does nothing.
        /// </summary>
        [Obsolete("This method no longer produces a Solution that does not share state and is no longer necessary to call.", error: false)]
        [EditorBrowsable(EditorBrowsableState.Never)] // hide this since it is obsolete and only leads to confusion
        public Solution GetIsolatedSolution()
        {
            // To maintain compat, just return ourself, which will be functionally identical.
            return this;
        }

        /// <summary>
        /// Creates a new solution instance with all the documents specified updated to have the same specified text.
        /// </summary>
        public Solution WithDocumentText(IEnumerable<DocumentId?> documentIds, SourceText text, PreservationMode mode = PreservationMode.PreserveValue)
        {
            if (documentIds == null)
            {
                throw new ArgumentNullException(nameof(documentIds));
            }

            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            if (!mode.IsValid())
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            var newCompilationState = _compilationState.WithDocumentText(documentIds, text, mode);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Returns a new Solution that will always produce a specific output for a generated file. This is used only in the
        /// implementation of <see cref="TextExtensions.GetOpenDocumentInCurrentContextWithChanges"/> where if a user has a source
        /// generated file open, we need to make sure everything lines up.
        /// </summary>
        internal Document WithFrozenSourceGeneratedDocument(SourceGeneratedDocumentIdentity documentIdentity, SourceText text)
        {
            var newCompilationState = _compilationState.WithFrozenSourceGeneratedDocument(documentIdentity, text);
            var newSolution = newCompilationState != _compilationState
                ? new Solution(newCompilationState)
                : this;

            var newDocumentState = newCompilationState.TryGetSourceGeneratedDocumentStateForAlreadyGeneratedId(documentIdentity.DocumentId);
            Contract.ThrowIfNull(newDocumentState, "Because we just froze this document, it should always exist.");

            var newProject = newSolution.GetRequiredProject(newDocumentState.Id.ProjectId);
            return newProject.GetOrCreateSourceGeneratedDocument(newDocumentState);
        }

        /// <summary>
        /// Undoes the operation of <see cref="WithFrozenSourceGeneratedDocument"/>; any frozen source generated document is allowed
        /// to have it's real output again.
        /// </summary>
        internal Solution WithoutFrozenSourceGeneratedDocuments()
        {
            var newCompilationState = _compilationState.WithoutFrozenSourceGeneratedDocuments();
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Returns a new Solution which represents the same state as before, but with the cached generator driver state from the given project updated to match.
        /// </summary>
        /// <remarks>
        /// When generators are ran in a Solution snapshot, they may cache state to speed up future runs. For Razor, we only run their generator on forked
        /// solutions that are thrown away; this API gives us a way to reuse that cached state in other forked solutions, since otherwise there's no way to reuse
        /// the cached state.
        /// </remarks>
        internal Solution WithCachedSourceGeneratorState(ProjectId projectToUpdate, Project projectWithCachedGeneratorState)
        {
            var newCompilationState = _compilationState.WithCachedSourceGeneratorState(projectToUpdate, projectWithCachedGeneratorState);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        /// <summary>
        /// Gets an objects that lists the added, changed and removed projects between
        /// this solution and the specified solution.
        /// </summary>
        public SolutionChanges GetChanges(Solution oldSolution)
        {
            if (oldSolution == null)
            {
                throw new ArgumentNullException(nameof(oldSolution));
            }

            return new SolutionChanges(this, oldSolution);
        }

        /// <summary>
        /// Gets the set of <see cref="DocumentId"/>s in this <see cref="Solution"/> with a
        /// <see cref="TextDocument.FilePath"/> that matches the given file path.
        /// </summary>
        public ImmutableArray<DocumentId> GetDocumentIdsWithFilePath(string? filePath) => this.SolutionState.GetDocumentIdsWithFilePath(filePath);

        /// <summary>
        /// Gets a <see cref="ProjectDependencyGraph"/> that details the dependencies between projects for this solution.
        /// </summary>
        public ProjectDependencyGraph GetProjectDependencyGraph() => this.SolutionState.GetProjectDependencyGraph();

        /// <summary>
        /// Returns the options that should be applied to this solution. This is equivalent to <see cref="Workspace.Options" /> when the <see cref="Solution"/> 
        /// instance was created.
        /// </summary>
        public OptionSet Options => this.SolutionState.Options;

        /// <summary>
        /// Analyzer references associated with the solution.
        /// </summary>
        public IReadOnlyList<AnalyzerReference> AnalyzerReferences => this.SolutionState.AnalyzerReferences;

        /// <summary>
        /// Creates a new solution instance with the specified <paramref name="options"/>.
        /// </summary>
        public Solution WithOptions(OptionSet options)
        {
            return options switch
            {
                SolutionOptionSet serializableOptions => WithOptions(serializableOptions),
                null => throw new ArgumentNullException(nameof(options)),
                _ => throw new ArgumentException(WorkspacesResources.Options_did_not_come_from_specified_Solution, paramName: nameof(options))
            };
        }

        /// <summary>
        /// Creates a new solution instance with the specified serializable <paramref name="options"/>.
        /// </summary>
        internal Solution WithOptions(SolutionOptionSet options)
        {
            var newCompilationState = _compilationState.WithOptions(options);
            return newCompilationState == _compilationState ? this : new Solution(newCompilationState);
        }

        private void CheckContainsProject(ProjectId projectId)
        {
            if (projectId == null)
            {
                throw new ArgumentNullException(nameof(projectId));
            }

            if (!ContainsProject(projectId))
            {
                throw new InvalidOperationException(WorkspacesResources.The_solution_does_not_contain_the_specified_project);
            }
        }

        private void CheckContainsDocument(DocumentId documentId)
        {
            if (documentId == null)
            {
                throw new ArgumentNullException(nameof(documentId));
            }

            if (!ContainsDocument(documentId))
            {
                throw new InvalidOperationException(WorkspaceExtensionsResources.The_solution_does_not_contain_the_specified_document);
            }
        }

        private void CheckContainsDocuments(ImmutableArray<DocumentId> documentIds)
        {
            if (documentIds.IsDefault)
            {
                throw new ArgumentNullException(nameof(documentIds));
            }

            foreach (var documentId in documentIds)
            {
                CheckContainsDocument(documentId);
            }
        }

        private void CheckContainsAdditionalDocument(DocumentId documentId)
        {
            if (documentId == null)
            {
                throw new ArgumentNullException(nameof(documentId));
            }

            if (!ContainsAdditionalDocument(documentId))
            {
                throw new InvalidOperationException(WorkspaceExtensionsResources.The_solution_does_not_contain_the_specified_document);
            }
        }

        private void CheckContainsAdditionalDocuments(ImmutableArray<DocumentId> documentIds)
        {
            if (documentIds.IsDefault)
            {
                throw new ArgumentNullException(nameof(documentIds));
            }

            foreach (var documentId in documentIds)
            {
                CheckContainsAdditionalDocument(documentId);
            }
        }

        private void CheckContainsAnalyzerConfigDocument(DocumentId documentId)
        {
            if (documentId == null)
            {
                throw new ArgumentNullException(nameof(documentId));
            }

            if (!ContainsAnalyzerConfigDocument(documentId))
            {
                throw new InvalidOperationException(WorkspaceExtensionsResources.The_solution_does_not_contain_the_specified_document);
            }
        }

        private void CheckContainsAnalyzerConfigDocuments(ImmutableArray<DocumentId> documentIds)
        {
            if (documentIds.IsDefault)
            {
                throw new ArgumentNullException(nameof(documentIds));
            }

            foreach (var documentId in documentIds)
            {
                CheckContainsAnalyzerConfigDocument(documentId);
            }
        }

        /// <summary>
        /// Throws if setting the project references of project <paramref name="projectId"/> to specified <paramref name="projectReferences"/>
        /// would form a cycle in project dependency graph.
        /// </summary>
        private void CheckCircularProjectReferences(ProjectId projectId, IReadOnlyCollection<ProjectReference> projectReferences)
        {
            foreach (var projectReference in projectReferences)
            {
                if (projectId == projectReference.ProjectId)
                {
                    throw new InvalidOperationException(WorkspacesResources.A_project_may_not_reference_itself);
                }

                if (this.SolutionState.ContainsTransitiveReference(projectReference.ProjectId, projectId))
                {
                    throw new InvalidOperationException(
                        string.Format(WorkspacesResources.Adding_project_reference_from_0_to_1_will_cause_a_circular_reference,
                            projectId,
                            projectReference.ProjectId));
                }
            }
        }

        /// <summary>
        /// Throws if setting the project references of project <paramref name="projectId"/> to specified <paramref name="projectReferences"/>
        /// would form an invalid submission project chain.
        /// 
        /// Submission projects can reference at most one other submission project. Regular projects can't reference any.
        /// </summary>
        private void CheckSubmissionProjectReferences(ProjectId projectId, IEnumerable<ProjectReference> projectReferences, bool ignoreExistingReferences)
        {
            var projectState = this.SolutionState.GetRequiredProjectState(projectId);

            var isSubmission = projectState.IsSubmission;
            var hasSubmissionReference = !ignoreExistingReferences && projectState.ProjectReferences.Any(p => this.SolutionState.GetRequiredProjectState(p.ProjectId).IsSubmission);

            foreach (var projectReference in projectReferences)
            {
                // Note: need to handle reference to a project that's not included in the solution:
                var referencedProjectState = this.SolutionState.GetProjectState(projectReference.ProjectId);
                if (referencedProjectState != null && referencedProjectState.IsSubmission)
                {
                    if (!isSubmission)
                    {
                        throw new InvalidOperationException(WorkspacesResources.Only_submission_project_can_reference_submission_projects);
                    }

                    if (hasSubmissionReference)
                    {
                        throw new InvalidOperationException(WorkspacesResources.This_submission_already_references_another_submission_project);
                    }

                    hasSubmissionReference = true;
                }
            }
        }
    }
}