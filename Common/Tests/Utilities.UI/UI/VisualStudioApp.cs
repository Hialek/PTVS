﻿// Visual Studio Shared Project
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudioTools.VSTestHost;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
using Task = System.Threading.Tasks.Task;

namespace TestUtilities.UI {
    /// <summary>
    /// Provides wrappers for automating the VisualStudio UI.
    /// </summary>
    public class VisualStudioApp : AutomationWrapper, IDisposable {
        private SolutionExplorerTree _solutionExplorerTreeView;
        private ObjectBrowser _objectBrowser, _resourceView;
        private AzureCloudServiceActivityLog _azureActivityLog;
        private IntPtr _mainWindowHandle;
        private readonly DTE _dte;
        private IServiceProvider _provider;
        private List<Action> _onDispose;
        private bool _isDisposed, _skipCloseAll;

        public VisualStudioApp(DTE dte = null)
            : this(new IntPtr((dte ?? VSTestContext.DTE).MainWindow.HWnd)) {
            _dte = dte ?? VSTestContext.DTE;

            foreach (var p in ((DTE2)_dte).ToolWindows.OutputWindow.OutputWindowPanes.OfType<OutputWindowPane>()) {
                p.Clear();
            }
        }

        private VisualStudioApp(IntPtr windowHandle)
            : base(AutomationElement.FromHandle(windowHandle)) {
            _mainWindowHandle = windowHandle;
        }

        public bool IsDisposed {
            get { return _isDisposed; }
        }

        public void OnDispose(Action action) {
            Debug.Assert(action != null);
            if (_onDispose == null) {
                _onDispose = new List<Action> { action };
            } else {
                _onDispose.Add(action);
            }
        }

        protected virtual void Dispose(bool disposing) {
            if (!_isDisposed) {
                _isDisposed = true;
                try {
                    if (_onDispose != null) {
                        foreach (var action in _onDispose) {
                            try {
                                action();
                            } catch (Exception ex) {
                                Debug.WriteLine("Exception calling action while disposing VisualStudioApp: {0}", ex);
                            }
                        }
                    }

                    if (_dte != null && _dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode) {
                        try {
                            _dte.Debugger.TerminateAll();
                            _dte.Debugger.Stop();
                        } catch (COMException ex) {
                            Debug.WriteLine("Exception disposing VisualStudioApp: {0}", ex);
                        }
                    }
                    DismissAllDialogs();
                    for (int i = 0; i < 100 && !_skipCloseAll; i++) {
                        try {
                            _dte.Solution.Close(false);
                            break;
                        } catch (Exception ex) {
                            Debug.WriteLine(ex.ToString());
                            _dte.Documents.CloseAll(EnvDTE.vsSaveChanges.vsSaveChangesNo);
                            System.Threading.Thread.Sleep(200);
                        }
                    }
                } catch (Exception ex) {
                    Debug.WriteLine("Exception disposing VisualStudioApp: {0}", ex);
                }

                AssertListener.ThrowUnhandled();
            }
        }

        public void Dispose() {
            Dispose(true);
        }

        public void SuppressCloseAllOnDispose() {
            _skipCloseAll = true;
        }

        public IComponentModel ComponentModel {
            get {
                return GetService<IComponentModel>(typeof(SComponentModel));
            }
        }

        public IServiceProvider ServiceProvider {
            get {
                if (_provider == null) {
                    if (_dte == null) {
                        _provider = VSTestContext.ServiceProvider;
                    } else {
                        _provider = new ServiceProvider((IOleServiceProvider)_dte);
                        OnDispose(() => ((ServiceProvider)_provider).Dispose());
                    }
                }
                return _provider;
            }
        }

        public T GetService<T>(Type type = null) {
            return (T)ServiceProvider.GetService(type ?? typeof(T));
        }

        /// <summary>
        /// File->Save
        /// </summary>
        public void SaveSelection() {
            Dte.ExecuteCommand("File.SaveSelectedItems");
        }

        /// <summary>
        /// Opens and activates the solution explorer window.
        /// </summary>
        public SolutionExplorerTree OpenSolutionExplorer() {
            _solutionExplorerTreeView = null;
            Dte.ExecuteCommand("View.SolutionExplorer");
            return SolutionExplorerTreeView;
        }

        /// <summary>
        /// Opens and activates the object browser window.
        /// </summary>
        public void OpenObjectBrowser() {
            Dte.ExecuteCommand("View.ObjectBrowser");
            Dte.ExecuteCommand("View.ObjectBrowserSortObjectsAlphabetically");
        }

        /// <summary>
        /// Opens and activates the Resource View window.
        /// </summary>
        public void OpenResourceView() {
            Dte.ExecuteCommand("View.ResourceView");
        }

        public IntPtr OpenDialogWithDteExecuteCommand(string commandName, string commandArgs = "") {
            Task task = Task.Factory.StartNew(() => {
                Dte.ExecuteCommand(commandName, commandArgs);
                Console.WriteLine("Successfully executed command {0} {1}", commandName, commandArgs);
            });

            IntPtr dialog = IntPtr.Zero;

            try {
                dialog = WaitForDialog(task);
            } finally {
                if (dialog == IntPtr.Zero) {
                    if (task.IsFaulted && task.Exception != null) {
                        Assert.Fail("Unexpected Exception - VSTestContext.DTE.ExecuteCommand({0},{1}){2}{3}",
                                commandName, commandArgs, Environment.NewLine, task.Exception.ToString());
                    }
                    Assert.Fail("Task failed - VSTestContext.DTE.ExecuteCommand({0},{1})",
                            commandName, commandArgs);
                }
            }
            return dialog;
        }

        public void ExecuteCommand(string commandName, string commandArgs = "", int timeout = 25000) {
            Task task = Task.Factory.StartNew(() => {
                Console.WriteLine("Executing command {0} {1}", commandName, commandArgs);
                Dte.ExecuteCommand(commandName, commandArgs);
                Console.WriteLine("Successfully executed command {0} {1}", commandName, commandArgs);
            });

            bool timedOut = false;
            try {
                timedOut = !task.Wait(timeout);
            } catch (AggregateException ae) {
                foreach (var ex in ae.InnerExceptions) {
                    Console.WriteLine(ex.ToString());
                }
                throw ae.InnerException;
            }

            if (timedOut) {
                string msg = String.Format("Command {0} failed to execute in specified timeout", commandName);
                Console.WriteLine(msg);
                DumpVS();
                Assert.Fail(msg);
            }
        }

        /// <summary>
        /// Opens and activates the Navigate To window.
        /// </summary>
        public NavigateToDialog OpenNavigateTo() {
            Task task = Task.Factory.StartNew(() => {
                Dte.ExecuteCommand("Edit.NavigateTo");
                Console.WriteLine("Successfully executed Edit.NavigateTo");
            });

            for (int retries = 10; retries > 0; --retries) {
#if DEV12_OR_LATER
                foreach (var element in Element.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "Window")
                ).OfType<AutomationElement>()) {
                    if (element.FindAll(TreeScope.Children, new OrCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_SearchHost"),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_ResultList")
                    )).Count == 2) {
                        return new NavigateToDialog(element);
                    }
                }
#else
                var element = Element.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                        new PropertyCondition(AutomationElement.NameProperty, "Navigate To")
                    )
                );
                if (element != null) {
                    return new NavigateToDialog(element);
                }
#endif
                System.Threading.Thread.Sleep(500);
            }
            Assert.Fail("Could not find Navigate To window");
            return null;
        }

        public SaveDialog SaveAs() {
            return SaveDialog.FromDte(this);
        }

        /// <summary>
        /// Gets the specified document.  Filename should be fully qualified filename.
        /// </summary>
        public EditorWindow GetDocument(string filename) {
            Debug.Assert(Path.IsPathRooted(filename));

            string windowName = Path.GetFileName(filename);
            var elem = GetDocumentTab(windowName);
            for (int retries = 5; retries > 0 && elem == null; retries -= 1) {
                System.Threading.Thread.Sleep(500);
                elem = GetDocumentTab(windowName);
            }

            Assert.IsNotNull(elem, "Unable to find window '{0}'", windowName);

            elem = elem.FindFirst(TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.ClassNameProperty,
                    "WpfTextView"
                )
            );

            return new EditorWindow(filename, elem);
        }

        public AutomationElement GetDocumentTab(string windowName) {
            var elem = Element.FindFirst(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(
                        AutomationElement.ClassNameProperty,
                        "TabItem"
                    ),
                    new PropertyCondition(
                        AutomationElement.NameProperty,
                        windowName
                    )
                )
            );
            if (elem == null) {
                // maybe the file has been modified, try again with a *
                elem = Element.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(
                            AutomationElement.ClassNameProperty,
                            "TabItem"
                        ),
                        new PropertyCondition(
                            AutomationElement.NameProperty,
                            windowName + "*"
                        )
                    )
                );
            }
            return elem;
        }

        /// <summary>
        /// Selects the given source control provider.  Name merely needs to be
        /// enough text to disambiguate from other source control providers.
        /// </summary>
        public void SelectSourceControlProvider(string providerName) {
            Element.SetFocus();

            using (var dialog = ToolsOptionsDialog.FromDte(this)) {
                dialog.SelectedView = "Source Control/Plug-in Selection";
                var currentSourceControl = new ComboBox(
                    dialog.FindByAutomationId("2001") // Current source control plug-in
                );

                currentSourceControl.SelectItem(providerName);

                dialog.OK();
            }
        }

        public NewProjectDialog FileNewProject() {
            var dialog = OpenDialogWithDteExecuteCommand("File.NewProject");
            return new NewProjectDialog(this, AutomationElement.FromHandle(dialog));
        }

        public AttachToProcessDialog OpenDebugAttach() {
            var dialog = OpenDialogWithDteExecuteCommand("Debug.AttachtoProcess");
            return new AttachToProcessDialog(dialog);
        }

        public OutputWindowPane GetOutputWindow(string name) {
            return ((DTE2)Dte).ToolWindows.OutputWindow.OutputWindowPanes.Item(name);
        }

        public IEnumerable<Window> OpenDocumentWindows {
            get {
                return Dte.Windows.OfType<Window>().Where(w => w.Document != null);
            }
        }


        public void WaitForBuildComplete(int timeout) {
            for (int i = 0; i < timeout; i += 500) {
                if (Dte.Solution.SolutionBuild.BuildState == vsBuildState.vsBuildStateDone) {
                    return;
                }
                System.Threading.Thread.Sleep(500);
            }

            throw new TimeoutException("Timeout waiting for build to complete");
        }

        public string GetOutputWindowText(string name) {
            var window = GetOutputWindow(name);
            var doc = window.TextDocument;
            doc.Selection.SelectAll();
            return doc.Selection.Text;
        }

        public void WaitForOutputWindowText(string name, string containsText, int timeout=5000) {
            for (int i = 0; i < timeout; i += 500) {
                var text = GetOutputWindowText(name);
                if (text.Contains(containsText)) {
                    return;
                }
                System.Threading.Thread.Sleep(500);
            }

            Assert.Fail("Failed to find {0} in output window {1}, found:\r\n{2}", containsText, name, GetOutputWindowText(name));
        }

        public void DismissAllDialogs() {
            int foundWindow = 2;

            while (foundWindow != 0) {
                IVsUIShell uiShell = GetService<IVsUIShell>(typeof(IVsUIShell));
                if (uiShell == null) {
                    return;
                }
                
                IntPtr hwnd;
                uiShell.GetDialogOwnerHwnd(out hwnd);

                for (int j = 0; j < 10 && hwnd == _mainWindowHandle; j++) {
                    System.Threading.Thread.Sleep(100);
                    uiShell.GetDialogOwnerHwnd(out hwnd);
                }

                //We didn't see any dialogs
                if (hwnd == IntPtr.Zero || hwnd == _mainWindowHandle) {
                    foundWindow--;
                    continue;
                }

                //MessageBoxButton.Abort
                //MessageBoxButton.Cancel
                //MessageBoxButton.No
                //MessageBoxButton.Ok
                //MessageBoxButton.Yes
                //The second parameter is going to be the value returned... We always send Ok
                Debug.WriteLine("Dismissing dialog");
                AutomationWrapper.DumpElement(AutomationElement.FromHandle(hwnd));
                NativeMethods.EndDialog(hwnd, new IntPtr(1));
            }
        }

        /// <summary>
        /// Waits for a modal dialog to take over VS's main window and returns the HWND for the dialog.
        /// </summary>
        /// <returns></returns>
        public IntPtr WaitForDialog(Task task) {
            return WaitForDialogToReplace(_mainWindowHandle, task);
        }

        public IntPtr WaitForDialog() {
            return WaitForDialogToReplace(_mainWindowHandle, null);
        }

        public ExceptionHelperDialog WaitForException() {
            var window = FindByName("Exception Helper Indicator Window");
            if (window != null) {
                var innerPane = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Pane
                    )
                );
                Assert.IsNotNull(innerPane);
                return new ExceptionHelperDialog(innerPane);
            }

            Assert.Fail("Failed to find exception helper window");
            return null;
        }

        public ExceptionHelperAdornment WaitForExceptionAdornment() {
            var control = FindByAutomationId("TheExceptionControl");
            if (control != null) {
                var parent = TreeWalker.RawViewWalker.GetParent(control);
                Assert.IsNotNull(parent);
                return new ExceptionHelperAdornment(parent);
            }

            Assert.Fail("Failed to find exception helper adornment");
            return null;
        }

        /// <summary>
        /// Waits for a modal dialog to take over a given window and returns the HWND for the new dialog.
        /// </summary>
        /// <returns>An IntPtr which should be interpreted as an HWND</returns>        
        public IntPtr WaitForDialogToReplace(IntPtr originalHwnd) {
            return WaitForDialogToReplace(originalHwnd, null);
        }

        /// <summary>
        /// Waits for a modal dialog to take over a given window and returns the HWND for the new dialog.
        /// </summary>
        /// <returns>An IntPtr which should be interpreted as an HWND</returns>        
        public IntPtr WaitForDialogToReplace(AutomationElement element) {
            return WaitForDialogToReplace(new IntPtr(element.Current.NativeWindowHandle), null);
        }

        private IntPtr WaitForDialogToReplace(IntPtr originalHwnd, Task task) {
            IVsUIShell uiShell = GetService<IVsUIShell>(typeof(IVsUIShell));
            IntPtr hwnd;
            uiShell.GetDialogOwnerHwnd(out hwnd);

            int timeout = task == null ? 10000 : 60000;

            while (timeout > 0 && hwnd == originalHwnd && (task == null || !(task.IsFaulted || task.IsCanceled))) {
                timeout -= 500;
                System.Threading.Thread.Sleep(500);
                uiShell.GetDialogOwnerHwnd(out hwnd);
            }

            if (task != null && (task.IsFaulted || task.IsCanceled)) {
                return IntPtr.Zero;
            }

            if (hwnd == originalHwnd) {
                DumpElement(AutomationElement.FromHandle(hwnd));
            }
            Assert.AreNotEqual(IntPtr.Zero, hwnd);
            Assert.AreNotEqual(originalHwnd, hwnd, "Main window still has focus");
            return hwnd;
        }

        /// <summary>
        /// Waits for the VS main window to receive the focus.
        /// </summary>
        /// <returns>
        /// True if the main window has the focus. Otherwise, false.
        /// </returns>
        public bool WaitForDialogDismissed(bool assertIfFailed = true, int timeout = 100000) {
            IVsUIShell uiShell = GetService<IVsUIShell>(typeof(IVsUIShell));
            IntPtr hwnd;
            uiShell.GetDialogOwnerHwnd(out hwnd);

            for (int i = 0; i < (timeout / 100) && hwnd != _mainWindowHandle; i++) {
                System.Threading.Thread.Sleep(100);
                uiShell.GetDialogOwnerHwnd(out hwnd);
            }

            if (assertIfFailed) {
                Assert.AreEqual(_mainWindowHandle, hwnd);
                return true;
            }
            return _mainWindowHandle == hwnd;
        }

        /// <summary>
        /// Waits for no dialog. If a dialog appears before the timeout expires
        /// then the test fails and the dialog is closed.
        /// </summary>
        public void WaitForNoDialog(TimeSpan timeout) {
            IVsUIShell uiShell = GetService<IVsUIShell>(typeof(IVsUIShell));
            IntPtr hwnd;
            uiShell.GetDialogOwnerHwnd(out hwnd);

            for (int i = 0; i < 100 && hwnd == _mainWindowHandle; i++) {
                System.Threading.Thread.Sleep((int)timeout.TotalMilliseconds / 100);
                uiShell.GetDialogOwnerHwnd(out hwnd);
            }

            if (hwnd != (IntPtr)_mainWindowHandle) {
                AutomationWrapper.DumpElement(AutomationElement.FromHandle(hwnd));
                NativeMethods.EndDialog(hwnd, (IntPtr)(int)MessageBoxButton.Cancel);
                Assert.Fail("Dialog appeared - see output for details");
            }
        }

        public static void CheckMessageBox(params string[] text) {
            CheckMessageBox(MessageBoxButton.Cancel, text);
        }

        public static void CheckMessageBox(MessageBoxButton button, params string[] text) {
            CheckAndDismissDialog(text, 65535, new IntPtr((int)button));
        }

        /// <summary>
        /// Checks the text of a dialog and dismisses it.
        /// 
        /// dlgField is the field to check the text of.
        /// buttonId is the button to press to dismiss.
        /// </summary>
        private static void CheckAndDismissDialog(string[] text, int dlgField, IntPtr buttonId) {
            var handle = new IntPtr(VSTestContext.DTE.MainWindow.HWnd);
            IVsUIShell uiShell = VSTestContext.ServiceProvider.GetService(typeof(IVsUIShell)) as IVsUIShell;
            IntPtr hwnd;
            uiShell.GetDialogOwnerHwnd(out hwnd);

            for (int i = 0; i < 20 && hwnd == handle; i++) {
                System.Threading.Thread.Sleep(500);
                uiShell.GetDialogOwnerHwnd(out hwnd);
            }

            Assert.AreNotEqual(IntPtr.Zero, hwnd, "hwnd is null, We failed to get the dialog");
            Assert.AreNotEqual(handle, hwnd, "hwnd is Dte.MainWindow, We failed to get the dialog");
            Console.WriteLine("Ending dialog: ");
            AutomationWrapper.DumpElement(AutomationElement.FromHandle(hwnd));
            Console.WriteLine("--------");
            try {
                StringBuilder title = new StringBuilder(4096);
                Assert.AreNotEqual(NativeMethods.GetDlgItemText(hwnd, dlgField, title, title.Capacity), (uint)0);

                string t = title.ToString();
                AssertUtil.Contains(t, text);
            } finally {
                NativeMethods.EndDialog(hwnd, buttonId);
            }
        }

        /// <summary>
        /// Provides access to Visual Studio's solution explorer tree view.
        /// </summary>
        public SolutionExplorerTree SolutionExplorerTreeView {
            get {
                if (_solutionExplorerTreeView == null) {
                    AutomationElement element = null;
                    for (int i = 0; i < 20 && element == null; i++) {
                        element = Element.FindFirst(TreeScope.Descendants,
                            new AndCondition(
                                new PropertyCondition(
                                    AutomationElement.ControlTypeProperty,
                                    ControlType.Pane
                                ),
                                new PropertyCondition(
                                    AutomationElement.NameProperty,
                                    "Solution Explorer"
                                )
                            )
                        );
                        if (element == null) {
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                    AutomationElement treeElement = null;
                    if (element != null) {
                        for (int i = 0; i < 20 && treeElement == null; i++) {
                            treeElement = element.FindFirst(TreeScope.Descendants,
                                new PropertyCondition(
                                    AutomationElement.ControlTypeProperty,
                                    ControlType.Tree
                                )
                            );
                            if (treeElement == null) {
                                System.Threading.Thread.Sleep(500);
                            }
                        }
                    }
                    _solutionExplorerTreeView = new SolutionExplorerTree(treeElement);
                }
                return _solutionExplorerTreeView;
            }
        }

        /// <summary>
        /// Provides access to Visual Studio's object browser.
        /// </summary>
        public ObjectBrowser ObjectBrowser {
            get {
                if (_objectBrowser == null) {
                    AutomationElement element = null;
                    for (int i = 0; i < 10 && element == null; i++) {
                        element = Element.FindFirst(TreeScope.Descendants,
                            new AndCondition(
                                new PropertyCondition(
                                    AutomationElement.ClassNameProperty,
                                    "ViewPresenter"
                                ),
                                new PropertyCondition(
                                    AutomationElement.NameProperty,
                                    "Object Browser"
                                )
                            )
                        );
                        if (element == null) {
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                    _objectBrowser = new ObjectBrowser(element);
                }
                return _objectBrowser;
            }
        }

        /// <summary>
        /// Provides access to Visual Studio's resource view.
        /// </summary>
        public ObjectBrowser ResourceView {
            get {
                if (_resourceView == null) {
                    AutomationElement element = null;
                    for (int i = 0; i < 10 && element == null; i++) {
                        element = Element.FindFirst(TreeScope.Descendants,
                            new AndCondition(
                                new PropertyCondition(
                                    AutomationElement.ClassNameProperty,
                                    "ViewPresenter"
                                ),
                                new PropertyCondition(
                                    AutomationElement.NameProperty,
                                    "Resource View"
                                )
                            )
                        );
                        if (element == null) {
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                    _resourceView = new ObjectBrowser(element);
                }
                return _resourceView;
            }
        }

        /// <summary>
        /// Provides access to Azure's VS Activity Log window.
        /// </summary>
        public AzureCloudServiceActivityLog AzureActivityLog {
            get {
                if (_azureActivityLog == null) {
                    AutomationElement element = null;
                    for (int i = 0; i < 10 && element == null; i++) {
                        element = Element.FindFirst(TreeScope.Descendants,
                            new AndCondition(
                                new PropertyCondition(
                                    AutomationElement.ClassNameProperty,
                                    "GenericPane"
                                ),
                                new OrCondition(
                                    new PropertyCondition(
                                        AutomationElement.NameProperty,
                                        "Microsoft Azure Activity Log"
                                    ),
                                    new PropertyCondition(
                                        AutomationElement.NameProperty,
                                        "Windows Azure Activity Log"
                                    )
                                )
                            )
                        );
                        if (element == null) {
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                    _azureActivityLog = new AzureCloudServiceActivityLog(element);
                }
                return _azureActivityLog;
            }
        }

        /// <summary>
        /// Produces a name which is compatible with x:Name requirements (starts with a letter/underscore, contains
        /// only letter, numbers, or underscores).
        /// </summary>
        public static string GetName(string title) {
            if (title.Length == 0) {
                return "InteractiveWindowHost";
            }

            StringBuilder res = new StringBuilder();
            if (!Char.IsLetter(title[0])) {
                res.Append('_');
            }

            foreach (char c in title) {
                if (Char.IsLetter(c) || Char.IsDigit(c) || c == '_') {
                    res.Append(c);
                }
            }
            res.Append("Host");
            return res.ToString();
        }

        public DTE Dte {
            get {
                return _dte;
            }
        }

        public void WaitForMode(dbgDebugMode mode) {
            for (int i = 0; i < 60 && Dte.Debugger.CurrentMode != mode; i++) {
                System.Threading.Thread.Sleep(500);
            }

            Assert.AreEqual(mode, VSTestContext.DTE.Debugger.CurrentMode);
        }

        public virtual Project CreateProject(
            string languageName,
            string templateName,
            string createLocation,
            string projectName,
            bool newSolution = true,
            bool suppressUI = true
        ) {
            var sln = (Solution2)Dte.Solution;
            var templatePath = sln.GetProjectTemplate(templateName, languageName);
            Assert.IsTrue(File.Exists(templatePath) || Directory.Exists(templatePath), string.Format("Cannot find template '{0}' for language '{1}'", templateName, languageName));

            var origName = projectName;
            var projectDir = Path.Combine(createLocation, projectName);
            for (int i = 1; Directory.Exists(projectDir); ++i) {
                projectName = string.Format("{0}{1}", origName, i);
                projectDir = Path.Combine(createLocation, projectName);
            }

            var previousSuppressUI = Dte.SuppressUI;
            try {
                Dte.SuppressUI = suppressUI;
                sln.AddFromTemplate(templatePath, projectDir, projectName, newSolution);
            } finally {
                Dte.SuppressUI = previousSuppressUI;
            }

            return sln.Projects.Cast<Project>().FirstOrDefault(p => p.Name == projectName);
        }

        private static IEnumerable<IVsProject> EnumerateLoadedProjects(IVsSolution solution) {
            IEnumHierarchies hierarchies;
            Guid guid = Guid.Empty;
            ErrorHandler.ThrowOnFailure((solution.GetProjectEnum(
                (uint)(__VSENUMPROJFLAGS.EPF_ALLINSOLUTION),
                ref guid,
                out hierarchies
            )));
            IVsHierarchy[] hierarchy = new IVsHierarchy[1];
            uint fetched;
            while (ErrorHandler.Succeeded(hierarchies.Next(1, hierarchy, out fetched)) && fetched == 1) {
                var project = hierarchy[0] as IVsProject;
                if (project != null) {
                    yield return project;
                }
            }
        }

        public Project OpenProject(
            string projName,
            string startItem = null,
            int? expectedProjects = null,
            string projectName = null,
            bool setStartupItem = true,
            Func<AutomationDialog, bool> onDialog = null
        ) {
            var solution = GetService<IVsSolution>(typeof(SVsSolution));
            var solution4 = solution as IVsSolution4;
            Assert.IsNotNull(solution, "Failed to obtain SVsSolution service");
            Assert.IsNotNull(solution4, "Failed to obtain IVsSolution4 interface");

            // Close any open solution
            string slnDir, slnFile, slnOpts;
            if (ErrorHandler.Succeeded(solution.GetSolutionInfo(out slnDir, out slnFile, out slnOpts))) {
                Console.WriteLine("Closing {0}", slnFile);
                solution.CloseSolutionElement(0, null, 0);
            }

            string fullPath = TestData.GetPath(projName);
            Assert.IsTrue(File.Exists(fullPath), "Cannot find " + fullPath);
            Console.WriteLine("Opening {0}", fullPath);

            // If there is a .suo file, delete that so that there is no state carried over from another test.
            for (int i = 10; i <= 15; ++i) {
                string suoPath = Path.ChangeExtension(fullPath, ".v" + i + ".suo");
                if (File.Exists(suoPath)) {
                    File.Delete(suoPath);
                }
            }
            var dotVsPath = Path.Combine(Path.GetDirectoryName(fullPath), ".vs");
            if (Directory.Exists(dotVsPath)) {
                foreach (var suoPath in FileUtils.EnumerateFiles(dotVsPath, ".vso")) {
                    File.Delete(suoPath);
                }
            }

            Task t;
            if (fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) {
                t = Task.Run(() => {
                    ErrorHandler.ThrowOnFailure(solution.OpenSolutionFile((uint)0, fullPath));
                    // Force all projects to load before running any tests.
                    solution4.EnsureSolutionIsLoaded((uint)__VSBSLFLAGS.VSBSLFLAGS_None);
                });
            } else {
                t = Task.Run(() => {
                    Guid guidNull = Guid.Empty;
                    Guid iidUnknown = Guid.Empty;
                    IntPtr projPtr;
                    ErrorHandler.ThrowOnFailure(solution.CreateProject(
                        ref guidNull,
                        fullPath,
                        "",
                        "",
                        (uint)__VSCREATEPROJFLAGS.CPF_OPENFILE |
                            (uint)__VSCREATEPROJFLAGS2.CPF_OPEN_STANDALONE |
                            (uint)__VSCREATEPROJFLAGS.CPF_SILENT,
                        ref iidUnknown,
                        out projPtr
                    ));
                    // Force all projects to load before running any tests.
                    solution4.EnsureSolutionIsLoaded((uint)__VSBSLFLAGS.VSBSLFLAGS_None);
                });
            }
            using (var cts = System.Diagnostics.Debugger.IsAttached ? new CancellationTokenSource() : new CancellationTokenSource(30000)) {
                try {
                    if (!t.Wait(1000, cts.Token)) {
                        // Load has taken a while, start checking whether a dialog has
                        // appeared
                        IVsUIShell uiShell = GetService<IVsUIShell>(typeof(IVsUIShell));
                        IntPtr hwnd;
                        while (!t.Wait(1000, cts.Token)) {
                            ErrorHandler.ThrowOnFailure(uiShell.GetDialogOwnerHwnd(out hwnd));
                            if (hwnd != _mainWindowHandle) {
                                using (var dlg = new AutomationDialog(this, AutomationElement.FromHandle(hwnd))) {
                                    if (onDialog == null || onDialog(dlg) == false) {
                                        Console.WriteLine("Unexpected dialog");
                                        DumpElement(dlg.Element);
                                        Assert.Fail("Unexpected dialog while loading project");
                                    }
                                }
                            }
                        }
                    }
                } catch (OperationCanceledException) {
                    Assert.Fail("Failed to open project quickly enough");
                }
            }

            var projects = EnumerateLoadedProjects(solution).ToList();
            
            if (expectedProjects != null && expectedProjects.Value != projects.Count) {
                // if we have other files open we can end up with a bonus project...
                Assert.AreEqual(
                    expectedProjects,
                    projects.Count(p => {
                        string mk;
                        return ErrorHandler.Succeeded(solution.GetUniqueNameOfProject((IVsHierarchy)p, out mk)) &&
                            mk != "Miscellaneous Files";
                    }),
                    "Wrong number of loaded projects"
                );
            }


            var vsProject = string.IsNullOrEmpty(projectName) ?
                projects.OfType<IVsHierarchy>().FirstOrDefault() :
                projects.OfType<IVsHierarchy>().FirstOrDefault(p => {
                string mk;
                if (ErrorHandler.Failed(solution.GetUniqueNameOfProject(p, out mk))) {
                    return false;
                }
                Console.WriteLine(mk);
                return Path.GetFileNameWithoutExtension(mk) == projectName;
            });

            string outputText = "(unable to get Solution output)";
            try {
                outputText = GetOutputWindowText("Solution");
            } catch (Exception) {
            }
            Assert.IsNotNull(vsProject, "No project loaded: " + outputText);

            Guid projGuid;
            ErrorHandler.ThrowOnFailure(solution.GetGuidOfProject(vsProject, out projGuid));

            object o;
            ErrorHandler.ThrowOnFailure(vsProject.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ExtObject, out o));
            var project = (Project)o;

            if (project.Properties == null) {
                ErrorHandler.ThrowOnFailure(solution4.ReloadProject(ref projGuid));
            }

            // HACK: Testing whether Properties is just slow to initialize
            for (int retries = 10; retries > 0 && project.Properties == null; --retries) {
                Trace.TraceWarning("Waiting for project.Properties to become non-null");
                System.Threading.Thread.Sleep(250);
            }
            Assert.IsNotNull(project.Properties, "No project properties: " + outputText);
            Assert.IsTrue(project.Properties.GetEnumerator().MoveNext(), "No items in project properties: " + outputText);

            if (startItem != null && setStartupItem) {
                project.SetStartupFile(startItem);
                for (var i = 0; i < 20; i++) {
                    //Wait for the startupItem to be set before returning from the project creation
                    try {
                        if (((string)project.Properties.Item("StartupFile").Value) == startItem) {
                            break;
                        }
                    } catch { }
                    System.Threading.Thread.Sleep(250);
                }
            }

            DeleteAllBreakPoints();

            return project;
        }

        public Project GetProject(string projectName) {
            var iter = Dte.Solution.Projects.GetEnumerator();
            if (!iter.MoveNext()) {
                return null;
            }

            Project project = (Project)iter.Current;
            if (projectName != null) {
                while (project.Name != projectName) {
                    Assert.IsTrue(iter.MoveNext(), "Failed to find project named " + projectName);
                    project = (Project)iter.Current;
                }
            }
            return project;
        }

        public void DeleteAllBreakPoints() {
            var debug3 = (EnvDTE90.Debugger3)Dte.Debugger;
            if (debug3.Breakpoints != null) {
                foreach (var bp in debug3.Breakpoints) {
                    ((EnvDTE90a.Breakpoint3)bp).Delete();
                }
            }
        }

        public Uri PublishToAzureCloudService(string serviceName, string subscriptionPublishSettingsFilePath) {
            using (var publishDialog = AzureCloudServicePublishDialog.FromDte(this)) {
                using (var manageSubscriptionsDialog = publishDialog.SelectManageSubscriptions()) {
                    LoadPublishSettings(manageSubscriptionsDialog, subscriptionPublishSettingsFilePath);
                    manageSubscriptionsDialog.Close();
                }

                publishDialog.ClickNext();

                using (var createServiceDialog = publishDialog.SelectCreateNewService()) {
                    createServiceDialog.ServiceName = serviceName;
                    createServiceDialog.Location = "West US";
                    createServiceDialog.ClickCreate();
                }

                publishDialog.ClickPublish();
            }

            return new Uri(string.Format("http://{0}.cloudapp.net", serviceName));
        }

        public Uri PublishToAzureWebSite(string siteName, string subscriptionPublishSettingsFilePath) {
            using (var publishDialog = AzureWebSitePublishDialog.FromDte(this)) {
                using (var importSettingsDialog = publishDialog.ClickImportSettings()) {
                    importSettingsDialog.ClickImportFromWindowsAzureWebSite();

                    using (var manageSubscriptionsDialog = importSettingsDialog.ClickImportOrManageSubscriptions()) {
                        LoadPublishSettings(manageSubscriptionsDialog, subscriptionPublishSettingsFilePath);
                        manageSubscriptionsDialog.Close();
                    }

                    using (var createSiteDialog = importSettingsDialog.ClickNew()) {
                        createSiteDialog.SiteName = siteName;
                        createSiteDialog.ClickCreate();
                    }

                    importSettingsDialog.ClickOK();
                }

                publishDialog.ClickPublish();
            }

            return new Uri(string.Format("http://{0}.azurewebsites.net", siteName));
        }

        private void LoadPublishSettings(AzureManageSubscriptionsDialog manageSubscriptionsDialog, string publishSettingsFilePath) {
            manageSubscriptionsDialog.ClickCertificates();

            while (manageSubscriptionsDialog.SubscriptionsListBox.Count > 0) {
                manageSubscriptionsDialog.SubscriptionsListBox[0].Select();
                manageSubscriptionsDialog.ClickRemove();
                WaitForDialogToReplace(manageSubscriptionsDialog.Element);
                VisualStudioApp.CheckMessageBox(TestUtilities.MessageBoxButton.Yes);
            }

            using (var importSubscriptionDialog = manageSubscriptionsDialog.ClickImport()) {
                importSubscriptionDialog.FileName = publishSettingsFilePath;
                importSubscriptionDialog.ClickImport();
            }
        }

        public List<IVsTaskItem> WaitForErrorListItems(int expectedCount) {
            return WaitForTaskListItems(typeof(SVsErrorList), expectedCount, exactMatch: false);
        }

        public List<IVsTaskItem> WaitForTaskListItems(Type taskListService, int expectedCount, bool exactMatch = true) {
            Console.Write("Waiting for {0} items on {1} ... ", expectedCount, taskListService.Name);

            var errorList = GetService<IVsTaskList>(taskListService);
            var allItems = new List<IVsTaskItem>();

            if (expectedCount == 0) {
                // Allow time for errors to appear. Otherwise when we expect 0
                // errors we will get a false pass.
                System.Threading.Thread.Sleep(5000);
            }

            for (int retries = 20; retries > 0; --retries) {
                allItems.Clear();
                IVsEnumTaskItems items;
                ErrorHandler.ThrowOnFailure(errorList.EnumTaskItems(out items));

                IVsTaskItem[] taskItems = new IVsTaskItem[1];

                uint[] itemCnt = new uint[1];

                while (ErrorHandler.Succeeded(items.Next(1, taskItems, itemCnt)) && itemCnt[0] == 1) {
                    allItems.Add(taskItems[0]);
                }
                if (allItems.Count >= expectedCount) {
                    break;
                }
                // give time for errors to process...
                System.Threading.Thread.Sleep(1000);
            }

            if (exactMatch) {
                Assert.AreEqual(expectedCount, allItems.Count);
            }

            return allItems;
        }

        internal ProjectItem AddItem(Project project, string language, string template, string filename) {
            var fullTemplate = ((Solution2)project.DTE.Solution).GetProjectItemTemplate(template, language);
            return project.ProjectItems.AddFromTemplate(fullTemplate, filename);
        }
    }
}
