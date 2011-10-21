﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Search;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Bookmarks;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.Search;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;

namespace SearchAndReplace
{
	/// <summary>
	/// Provides all search actions: find next, find all, mark all, mark result
	/// </summary>
	public class SearchManager
	{
		static IEnumerable<FileName> GenerateFileList(SearchTarget target, string baseDirectory = null, string filter = "*.*", bool searchSubdirs = false)
		{
			List<FileName> files = new List<FileName>();
			
			switch (target) {
				case SearchTarget.CurrentDocument:
				case SearchTarget.CurrentSelection:
					ITextEditorProvider vc = WorkbenchSingleton.Workbench.ActiveViewContent as ITextEditorProvider;
					if (vc != null)
						files.Add(vc.TextEditor.FileName);
					break;
				case SearchTarget.AllOpenFiles:
					foreach (ITextEditorProvider editor in WorkbenchSingleton.Workbench.ViewContentCollection.OfType<ITextEditorProvider>())
						files.Add(editor.TextEditor.FileName);
					break;
				case SearchTarget.WholeProject:
					if (ProjectService.CurrentProject == null)
						break;
					foreach (FileProjectItem item in ProjectService.CurrentProject.Items.OfType<FileProjectItem>())
						files.Add(new FileName(item.FileName));
					break;
				case SearchTarget.WholeSolution:
					if (ProjectService.OpenSolution == null)
						break;
					foreach (var item in ProjectService.OpenSolution.SolutionFolderContainers.Select(f => f.SolutionItems).SelectMany(si => si.Items))
						files.Add(new FileName(Path.Combine(ProjectService.OpenSolution.Directory, item.Location)));
					foreach (var item in ProjectService.OpenSolution.Projects.SelectMany(p => p.Items).OfType<FileProjectItem>())
						files.Add(new FileName(item.FileName));
					break;
				case SearchTarget.Directory:
					if (!Directory.Exists(baseDirectory))
						break;
					return FileUtility.LazySearchDirectory(baseDirectory, filter, searchSubdirs);
				default:
					throw new Exception("Invalid value for FileListType");
			}
			
			return files.Distinct();
		}
		
		public static void ShowSearchResults(string pattern, IObservable<SearchResultMatch> results)
		{
			string title = StringParser.Parse("${res:MainWindow.Windows.SearchResultPanel.OccurrencesOf}",
			                                  new StringTagPair("Pattern", pattern));
			SearchResultsPad.Instance.ShowSearchResults(title, results);
			SearchResultsPad.Instance.BringToFront();
		}
		
		public static IObservable<SearchResultMatch> FindAll(string pattern, bool ignoreCase, bool matchWholeWords, SearchMode mode,
		                                                     SearchTarget target, string baseDirectory = null, string filter = "*.*", bool searchSubdirs = false, bool useParallel = true, IProgressMonitor progressMonitor = null)
		{
			currentSearchRegion = null;
			CancellationToken ct = progressMonitor != null ? progressMonitor.CancellationToken : new CancellationTokenSource().Token;
			var monitor = progressMonitor ?? WorkbenchSingleton.Workbench.StatusBar.CreateProgressMonitor(ct);
			monitor.TaskName = "Find all occurrences of '" + pattern + "' in " + GetTargetDescription(target, baseDirectory);
			monitor.Status = OperationStatus.Normal;
			var strategy = SearchStrategyFactory.Create(pattern, ignoreCase, matchWholeWords, mode);
			ParseableFileContentFinder fileFinder = new ParseableFileContentFinder();
			IEnumerable<FileName> fileList = GenerateFileList(target, baseDirectory, filter, searchSubdirs);
			return new SearchRun(strategy, fileFinder, fileList, monitor);
		}
		
		static string GetTargetDescription(SearchTarget target, string baseDirectory = null)
		{
			switch (target) {
				case SearchTarget.CurrentDocument:
					return "the current document";
				case SearchTarget.CurrentSelection:
					return "the current selection";
				case SearchTarget.AllOpenFiles:
					return "all open files";
				case SearchTarget.WholeProject:
					return "the whole project";
				case SearchTarget.WholeSolution:
					return "the whole solution";
				case SearchTarget.Directory:
					return "the directory '" + baseDirectory + "'";
				default:
					throw new Exception("Invalid value for SearchTarget");
			}
		}
		
		class SearchRun : IObservable<SearchResultMatch>, IDisposable
		{
			IObserver<SearchResultMatch> observer;
			ISearchStrategy strategy;
			ParseableFileContentFinder fileFinder;
			IEnumerable<FileName> fileList;
			IProgressMonitor monitor;
			int count;
			
			public bool UseParallel { get; set; }
			
			public SearchRun(ISearchStrategy strategy, ParseableFileContentFinder fileFinder, IEnumerable<FileName> fileList, IProgressMonitor monitor)
			{
				this.strategy = strategy;
				this.fileFinder = fileFinder;
				this.fileList = fileList;
				this.monitor = monitor;
			}
			
			public IDisposable Subscribe(IObserver<SearchResultMatch> observer)
			{
				this.observer = observer;
				if (UseParallel) {
					var task = new System.Threading.Tasks.Task(
						delegate {
							var list = fileList.ToList();
							this.count = list.Count;
							Parallel.ForEach(list, new ParallelOptions { CancellationToken = monitor.CancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount }, fileName => SearchFile(fileName, strategy, monitor.CancellationToken));
						});
					task.ContinueWith(t => { if (t.Exception != null) observer.OnError(t.Exception); else observer.OnCompleted(); this.Dispose(); });
					task.Start();
				} else {
					foreach (var file in fileList) {
						SearchFile(file, strategy, monitor.CancellationToken);
					}
				}
				return this;
			}
			
			public void Dispose()
			{
				monitor.Dispose();
			}
			
			void SearchFile(FileName fileName, ISearchStrategy strategy, CancellationToken ct)
			{
				ITextBuffer buffer = fileFinder.Create(fileName);
				if (buffer == null)
					return;
				
				if (!MimeTypeDetection.FindMimeType(buffer).StartsWith("text/", StringComparison.Ordinal))
					return;
				var source = DocumentUtilitites.GetTextSource(buffer);
				TextDocument document = null;
				DocumentHighlighter highlighter = null;
				foreach(var result in strategy.FindAll(source, 0, source.TextLength)) {
					ct.ThrowIfCancellationRequested();
					if (document == null) {
						document = new TextDocument(source);
						var highlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(fileName));
						if (highlighting != null)
							highlighter = new DocumentHighlighter(document, highlighting.MainRuleSet);
					}
					var start = document.GetLocation(result.Offset).ToLocation();
					var end = document.GetLocation(result.Offset + result.Length).ToLocation();
					lock (observer)
						observer.OnNext(new SearchResultMatch(fileName, start, end, result.Offset, result.Length, SearchResultsPad.CreateInlineBuilder(start, end, document, Path.GetExtension(fileName))));
				}
				lock (monitor)
					monitor.Progress += 1.0 / count;
			}
		}
		
		public static ITextEditor GetActiveTextEditor()
		{
			ITextEditorProvider provider = WorkbenchSingleton.Workbench.ActiveViewContent as ITextEditorProvider;
			if (provider != null) {
				return provider.TextEditor;
			} else {
				return null;
			}
		}
		
		static SearchRegion currentSearchRegion;
		
		public static SearchResultMatch FindNext(string pattern, bool ignoreCase, bool matchWholeWords, SearchMode mode,
		                                         SearchTarget target, string baseDirectory = null, string filter = "*.*", bool searchSubdirs = false)
		{
			if (string.IsNullOrEmpty(pattern))
				return null;
			var files = GenerateFileList(target, baseDirectory, filter, searchSubdirs).ToArray();
			if (files.Length == 0)
				return null;
			if (currentSearchRegion == null || !currentSearchRegion.IsSameState(files, pattern, ignoreCase, matchWholeWords, mode,
			                                                                    target, baseDirectory, filter, searchSubdirs))
				currentSearchRegion = SearchRegion.CreateSearchRegion(files, pattern, ignoreCase, matchWholeWords, mode,
				                                                      target, baseDirectory, filter, searchSubdirs);
			if (currentSearchRegion == null)
				return null;
			var result = currentSearchRegion.FindNext();
			if (result == null)
				currentSearchRegion = null;
			return result;
		}
		
		class SearchRegion
		{
			FileName[] files;
			AnchorSegment selection;
			ISearchStrategy strategy;
			
			public SearchResultMatch FindNext()
			{
				// Setup search inside current or first file.
				ParseableFileContentFinder finder = new ParseableFileContentFinder();
				int index = GetCurrentFileIndex();
				int i = 0;
				int searchOffset = 0;

				SearchResultMatch result = null;
				
				if (index > -1) {
					ITextEditor editor = GetActiveTextEditor();
					if (editor.SelectionLength > 0)
						searchOffset = editor.SelectionStart + editor.SelectionLength;
					else
						searchOffset = editor.Caret.Offset + 1;
					var document = editor.Document;
					
					int length;
					// if (target == SearchTarget.CurrentSelection) selection will not be null
					// hence use the selection as search region.
					if (selection != null) {
						searchOffset = Math.Max(selection.Offset, searchOffset);
						length = selection.EndOffset - searchOffset;
					} else {
						length = document.TextLength - searchOffset;
					}
					
					// try to find a result
					if (length > 0 && (searchOffset + length) <= document.TextLength)
						result = Find(editor.FileName, document, searchOffset, length);
				}
				
				// try the other files until we find something, or have processed all of them
				while (result == null && i < files.Length) {
					index = (index + 1) % files.Length;
					
					FileName file = files[index];
					ITextBuffer buffer = finder.Create(file);
					
					if (buffer == null)
						continue;
					int length;
					if (selection != null) {
						searchOffset = selection.Offset;
						length = selection.Length;
					} else {
						searchOffset = 0;
						length = buffer.TextLength;
					}
					
					// try to find a result
					result = Find(file, DocumentUtilitites.LoadReadOnlyDocumentFromBuffer(buffer), searchOffset, length);
					
					i++;
				}
				
				return result;
			}

			SearchResultMatch Find(FileName file, IDocument document, int searchOffset, int length)
			{
				var result = strategy.FindNext(DocumentUtilitites.GetTextSource(document), searchOffset, length);
				if (result != null) {
					var start = document.OffsetToPosition(result.Offset);
					var end = document.OffsetToPosition(result.EndOffset);
					return new SearchResultMatch(file, start, end, result.Offset, result.Length, null);
				}
				return null;
			}
			
			int GetCurrentFileIndex()
			{
				var editor = GetActiveTextEditor();
				if (editor == null)
					return -1;
				return files.FindIndex(file => editor.FileName.Equals(file));
			}
			
			public static SearchRegion CreateSearchRegion(FileName[] files, string pattern, bool ignoreCase, bool matchWholeWords, SearchMode mode, SearchTarget target, string baseDirectory, string filter, bool searchSubdirs)
			{
				ITextEditor editor = GetActiveTextEditor();
				if (editor != null) {
					var document = new TextDocument(DocumentUtilitites.GetTextSource(editor.Document));
					AnchorSegment selection = null;
					if (target == SearchTarget.CurrentSelection)
						selection = new AnchorSegment(document, editor.SelectionStart, editor.SelectionLength);
					return new SearchRegion(files, selection, pattern, ignoreCase, matchWholeWords, mode, target, baseDirectory, filter, searchSubdirs) { strategy = SearchStrategyFactory.Create(pattern, ignoreCase, matchWholeWords, mode) };
				}
				
				return null;
			}
			
			SearchRegion(FileName[] files, AnchorSegment selection, string pattern, bool ignoreCase, bool matchWholeWords, SearchMode mode, SearchTarget target, string baseDirectory, string filter, bool searchSubdirs)
			{
				if (files == null)
					throw new ArgumentNullException("files");
				this.files = files;
				this.selection = selection;
				this.pattern = pattern;
				this.ignoreCase = ignoreCase;
				this.matchWholeWords = matchWholeWords;
				this.mode = mode;
				this.target = target;
				this.baseDirectory = baseDirectory;
				this.filter = filter;
				this.searchSubdirs = searchSubdirs;
			}
			
			string pattern;
			bool ignoreCase;
			bool matchWholeWords;
			SearchMode mode;
			SearchTarget target;
			string baseDirectory;
			string filter;
			bool searchSubdirs;
			
			public bool IsSameState(FileName[] files, string pattern, bool ignoreCase, bool matchWholeWords, SearchMode mode, SearchTarget target, string baseDirectory, string filter, bool searchSubdirs)
			{
				return this.files.SequenceEqual(files) &&
					pattern == this.pattern &&
					ignoreCase == this.ignoreCase &&
					matchWholeWords == this.matchWholeWords &&
					mode == this.mode &&
					target == this.target &&
					baseDirectory == this.baseDirectory &&
					filter == this.filter &&
					searchSubdirs == this.searchSubdirs;
			}
		}
		
		public static void MarkAll(IObservable<SearchResultMatch> results)
		{
			int count = 0;
			results.ObserveOnUIThread()
				.Subscribe(
					match => { count++; MarkResult(match, false); },
					error => MessageService.ShowException(error),
					() => ShowMarkDoneMessage(count)
				);
		}
		
		public static void ReplaceAll(IObservable<SearchResultMatch> results, string replacement, CancellationToken ct)
		{
			var differences = new Dictionary<FileName, int>();
			int count = 0;
			results.ObserveOnUIThread()
				.Subscribe(
					match => {
						ITextEditor textArea = OpenTextArea(match.FileName, false);
						if (textArea != null) {
							int difference = 0;
							if (!differences.TryGetValue(match.FileName, out difference))
								differences.Add(match.FileName, 0);
							string newString = match.TransformReplacePattern(replacement);
							textArea.Document.Replace(match.StartOffset + difference, match.Length, newString);
							difference += newString.Length - match.Length;
							differences[match.FileName] = difference;
							count++;
						}
					},
					error => MessageService.ShowException(error),
					() => ShowReplaceDoneMessage(count)
				);
		}

		static void MarkResult(SearchResultMatch result, bool switchToOpenedView = true)
		{
			ITextEditor textArea = OpenTextArea(result.FileName, switchToOpenedView);
			if (textArea != null) {
				if (switchToOpenedView)
					textArea.Caret.Position = result.StartLocation;

				foreach (var bookmark in BookmarkManager.GetBookmarks(result.FileName)) {
					if (bookmark.CanToggle && bookmark.LineNumber == result.StartLocation.Line) {
						// bookmark or breakpoint already exists at that line
						return;
					}
				}
				BookmarkManager.AddMark(new Bookmark(result.FileName, result.StartLocation));
			}
		}

		static ITextEditor OpenTextArea(string fileName, bool switchToOpenedView = true)
		{
			ITextEditorProvider textEditorProvider;
			if (fileName != null) {
				textEditorProvider = FileService.OpenFile(fileName, switchToOpenedView) as ITextEditorProvider;
			} else {
				textEditorProvider = WorkbenchSingleton.Workbench.ActiveViewContent as ITextEditorProvider;
			}

			if (textEditorProvider != null) {
				return textEditorProvider.TextEditor;
			}
			return null;
		}
		
		static void ShowReplaceDoneMessage(int count)
		{
			if (count == 0) {
				ShowNotFoundMessage();
			} else {
				MessageService.ShowMessage(
					StringParser.Parse("${res:ICSharpCode.TextEditor.Document.SearchReplaceManager.ReplaceAllDone}",
					                   new StringTagPair("Count", count.ToString())),
					"${res:Global.FinishedCaptionText}");
			}
		}
		
		static void ShowMarkDoneMessage(int count)
		{
			if (count == 0) {
				ShowNotFoundMessage();
			} else {
				MessageService.ShowMessage(
					StringParser.Parse("${res:ICSharpCode.TextEditor.Document.SearchReplaceManager.MarkAllDone}",
					                   new StringTagPair("Count", count.ToString())),
					"${res:Global.FinishedCaptionText}");
			}
		}

		static void ShowNotFoundMessage()
		{
			MessageService.ShowMessage("${res:Dialog.NewProject.SearchReplace.SearchStringNotFound}", "${res:Dialog.NewProject.SearchReplace.SearchStringNotFound.Title}");
		}

		public static void SelectResult(SearchResultMatch result)
		{
			if (result == null) {
				ShowNotFoundMessage();
				return;
			}
			var editor = OpenTextArea(result.FileName, true);
			var start = editor.Document.PositionToOffset(result.StartLocation.Line, result.StartLocation.Column);
			var end = editor.Document.PositionToOffset(result.EndLocation.Line, result.EndLocation.Column);
			if (editor != null) {
				editor.Caret.Offset = start;
				editor.Select(start, end - start);
			}
		}
		
		public static bool IsResultSelected(SearchResultMatch result)
		{
			if (result == null)
				return false;
			var editor = GetActiveTextEditor();
			if (editor == null)
				return false;
			return result.FileName.Equals(editor.FileName)
				&& result.StartOffset == editor.SelectionStart
				&& result.Length == editor.SelectionLength;
		}
		
		public static void Replace(SearchResultMatch lastMatch, string replacement)
		{
			if (lastMatch == null)
				return;
			ITextEditor textArea = OpenTextArea(lastMatch.FileName);
			if (textArea != null)
				textArea.Document.Replace(lastMatch.StartOffset, lastMatch.Length, lastMatch.TransformReplacePattern(replacement));
		}
	}
}
