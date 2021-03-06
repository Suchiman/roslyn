﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.AddFileBanner
{
    internal abstract class AbstractAddFileBannerCodeRefactoringProvider : CodeRefactoringProvider
    {
        protected abstract bool IsCommentStartCharacter(char ch);

        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var cancellationToken = context.CancellationToken;
            var document = context.Document;

            if (!context.Span.IsEmpty)
            {
                return;
            }

            var position = context.Span.Start;
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var firstToken = root.GetFirstToken();
            if (!firstToken.FullSpan.IntersectsWith(position))
            {
                return;
            }

            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();
            var banner = syntaxFacts.GetFileBanner(root);

            if (banner.Length > 0)
            {
                // Already has a banner.
                return;
            }

            // Process the other documents in this document's project.  Look at the
            // ones that we can get a root from (without having to parse).  Then
            // look at the ones we'd need to parse.
            var siblingDocumentsAndRoots =
                document.Project.Documents
                        .Where(d => d != document)
                        .Select(d =>
                        {
                            d.TryGetSyntaxRoot(out var siblingRoot);
                            return (document: d, root: siblingRoot);
                        })
                        .OrderBy((t1, t2) => (t1.root != null) == (t2.root != null) ? 0 : t1.root != null ? -1 : 1);

            foreach (var (siblingDocument, siblingRoot) in siblingDocumentsAndRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var siblingBanner = await TryGetBannerAsync(siblingDocument, siblingRoot, cancellationToken).ConfigureAwait(false);
                if (siblingBanner.Length > 0 && !siblingDocument.IsGeneratedCode(cancellationToken))
                {
                    context.RegisterRefactoring(
                        new MyCodeAction(c => AddBannerAsync(document, root, siblingBanner, c)));
                    return;
                }
            }
        }

        private Task<Document> AddBannerAsync(
            Document document, SyntaxNode root, ImmutableArray<SyntaxTrivia> banner, CancellationToken c)
        {
            var newRoot = root.WithPrependedLeadingTrivia(new SyntaxTriviaList(banner));
            return Task.FromResult(document.WithSyntaxRoot(newRoot));
        }

        private async Task<ImmutableArray<SyntaxTrivia>> TryGetBannerAsync(
            Document document, SyntaxNode root, CancellationToken cancellationToken)
        {
            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();

            // If we have a tree already for this document, then just check to see
            // if it has a banner.
            if (root != null)
            {
                return syntaxFacts.GetFileBanner(root);
            }

            // Didn't have a tree.  Don't want to parse the file if we can avoid it.
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length == 0 || !IsCommentStartCharacter(text[0]))
            {
                // Didn't start with a comment character, don't bother looking at 
                // this file.
                return ImmutableArray<SyntaxTrivia>.Empty;
            }

            var token = syntaxFacts.ParseToken(text.ToString());
            return syntaxFacts.GetFileBanner(token);
        }

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(FeaturesResources.Add_file_banner, createChangedDocument)
            {
            }
        }
    }
}
