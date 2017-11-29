﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Text;
using Stunts.Processors;
using Stunts.Properties;

namespace Stunts
{
    public class StuntGenerator
    {
        static readonly string StuntsAssembly = Path.GetFileName(typeof(IStunt).Assembly.ManifestModule.FullyQualifiedName);

        string targetNamespace;
        // Configured processors, by language, then phase.
        Dictionary<string, Dictionary<ProcessorPhase, IDocumentProcessor[]>> processors;

        /// <summary>
        /// Instantiates the set of default <see cref="IDocumentProcessor"/> for the generator, 
        /// used for example when using the default constructor <see cref="StuntGenerator()"/>.
        /// </summary>
        /// <returns></returns>
        public static IDocumentProcessor[] GetDefaultProcessors() => new IDocumentProcessor[]
        {
            new CSharpScaffold(),
            new VisualBasicScaffold(),
            new CSharpRewrite(),
            new VisualBasicRewrite(),
            new VisualBasicParameterFixup(),
        };

        public StuntGenerator() : this(StuntNaming.StuntsNamespace, GetDefaultProcessors()) { }

        protected StuntGenerator(string targetNamespace, IDocumentProcessor[] processors)
        {
            this.targetNamespace = targetNamespace;
            this.processors = processors
                .GroupBy(processor => processor.Language)
                .ToDictionary(
                    bylang => bylang.Key,
                    bylang => bylang
                        .GroupBy(processor => processor.Phase)
                        .ToDictionary(byphase => byphase.Key, byphase => byphase.ToArray()));
        }

        /// <summary>
        /// Gets the canonical name for a stunt based on its base type and implemented interfaces.
        /// </summary>
        public virtual string GetStuntName(INamedTypeSymbol baseType, ImmutableArray<INamedTypeSymbol> implementedInterfaces) => StuntSymbolNaming.GetName(baseType, implementedInterfaces);

        public virtual async Task<Document> GenerateDocumentAsync(Project project, ITypeSymbol[] types, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!project.MetadataReferences.Any(r => r.Display.EndsWith(StuntsAssembly, StringComparison.Ordinal)))
                throw new ArgumentException(Strings.StuntsRequired(project.Name));

            cancellationToken.ThrowIfCancellationRequested();

            var generator = SyntaxGenerator.GetGenerator(project);
            var (baseType, implementedInterfaces) = ValidateTypes(types);

            var (name, syntax) = CreateStunt(baseType, implementedInterfaces, generator);
            var code = syntax.NormalizeWhitespace().ToFullString();

            var filePath = Path.GetTempFileName();
#if DEBUG
            File.WriteAllText(filePath, code);
#endif

            Document document;

            if (project.Solution.Workspace is AdhocWorkspace workspace)
            {
                document = workspace.AddDocument(DocumentInfo.Create(
                    DocumentId.CreateNewId(project.Id),
                    name,
                    folders: targetNamespace.Split('.'),
                    filePath: filePath,
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(code), VersionStamp.Create()))));
            }
            else
            {
                document = project.AddDocument(
                    name,
                    SourceText.From(code),
                    folders: targetNamespace.Split('.'),
                    filePath: filePath);
            }

            document = await ApplyVisitors(document, cancellationToken).ConfigureAwait(false);

#if DEBUG
            File.WriteAllText(filePath, code);
#endif

            return document;
        }

        public virtual (string name, SyntaxNode syntax) CreateStunt(INamedTypeSymbol baseType, ImmutableArray<INamedTypeSymbol> implementedInterfaces, SyntaxGenerator generator)
        {
            var name = GetStuntName(baseType, implementedInterfaces);
            var imports = new HashSet<string>
            {
                typeof(EventArgs).Namespace,
                typeof(ObservableCollection<>).Namespace,
                typeof(MethodBase).Namespace,
                typeof(IStunt).Namespace,
                typeof(CompilerGeneratedAttribute).Namespace,
            };

            if (baseType != null && baseType.ContainingNamespace != null && baseType.ContainingNamespace.CanBeReferencedByName)
                imports.Add(baseType.ContainingNamespace.ToDisplayString());

            foreach (var iface in implementedInterfaces.Where(i => i.ContainingNamespace != null && i.ContainingNamespace.CanBeReferencedByName))
            {
                imports.Add(iface.ContainingNamespace.ToDisplayString());
            }

            var syntax = generator.CompilationUnit(imports
                .Select(generator.NamespaceImportDeclaration)
                .Concat(new[]
                {
                    generator.NamespaceDeclaration(targetNamespace,
                        generator.AddAttributes(
                            generator.ClassDeclaration(name,
                                accessibility: Accessibility.Public,
                                modifiers: DeclarationModifiers.Partial,
                                baseType: baseType == null ? null : generator.IdentifierName(baseType.Name),
                                interfaceTypes: implementedInterfaces
                                    .Select(x => generator.IdentifierName(x.Name))
                            )
                        )
                    )
                }));

            return (name, syntax);
        }

        public virtual async Task<Document> ApplyVisitors(Document document, CancellationToken cancellationToken)
        {
#if DEBUG
            if (Debugger.IsAttached)
                cancellationToken = CancellationToken.None;
#endif

            var language = document.Project.Language;
            if (!processors.TryGetValue(language, out var supportedProcessors))
                return document;

            if (supportedProcessors.TryGetValue(ProcessorPhase.Prepare, out var prepares))
            {
                foreach (var prepare in prepares)
                {
                    document = await prepare.ProcessAsync(document, cancellationToken).ConfigureAwait(false);
                }
            }

            if (supportedProcessors.TryGetValue(ProcessorPhase.Scaffold, out var scaffolds))
            {
                foreach (var scaffold in scaffolds)
                {
                    document = await scaffold.ProcessAsync(document, cancellationToken).ConfigureAwait(false);
                }
            }

            if (supportedProcessors.TryGetValue(ProcessorPhase.Rewrite, out var rewriters))
            {
                foreach (var rewriter in rewriters)
                {
                    document = await rewriter.ProcessAsync(document, cancellationToken).ConfigureAwait(false);
                }
            }

            if (supportedProcessors.TryGetValue(ProcessorPhase.Fixup, out var fixups))
            {
                foreach (var fixup in fixups)
                {
                    document = await fixup.ProcessAsync(document, cancellationToken).ConfigureAwait(false);
                }
            }

            return document;
        }

        static (INamedTypeSymbol baseType, ImmutableArray<INamedTypeSymbol> additionalInterfaces) ValidateTypes(ITypeSymbol[] types)
        {
            var baseType = default(INamedTypeSymbol);
            var additionalInterfaces = default(IEnumerable<INamedTypeSymbol>);
            if (types[0].TypeKind == TypeKind.Class)
            {
                baseType = (INamedTypeSymbol)types[0];
                if (types.Skip(1).Any(x => x.TypeKind == TypeKind.Class))
                    throw new ArgumentException(Strings.WrongStuntBaseType(string.Join(",", types.Select(x => x.Name))));
                if (types.Skip(1).Any(x => x.TypeKind != TypeKind.Interface))
                    throw new ArgumentException(Strings.InvalidStuntTypes(string.Join(",", types.Select(x => x.Name))));

                additionalInterfaces = types.Skip(1).Cast<INamedTypeSymbol>();
            }
            else
            {
                if (types.Any(x => x.TypeKind == TypeKind.Class))
                    throw new ArgumentException(Strings.WrongStuntBaseType(string.Join(",", types.Select(x => x.Name))));
                if (types.Any(x => x.TypeKind != TypeKind.Interface))
                    throw new ArgumentException(Strings.InvalidStuntTypes(string.Join(",", types.Select(x => x.Name))));

                additionalInterfaces = types.Cast<INamedTypeSymbol>();
            }

            return (baseType, additionalInterfaces.OrderBy(x => x.Name).ToImmutableArray());
        }
    }
}
