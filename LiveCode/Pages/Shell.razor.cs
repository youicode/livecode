using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Blazor.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.JSInterop;

namespace LiveCode.Pages
{
    public partial class Shell
    {
        private ElementReference inputElem;

        public bool Disabled { get; set; } = true;
        public string Output { get; set; } = "";
        public string Input { get; set; } = "";

        private CSharpCompilation _previousCompilation;
        private IEnumerable<MetadataReference> _references;
        private object[] _submissionStates = new object[] { null, null };
        private int _submissionIndex = 0;
        private List<string> _history = new List<string>();
        private int _historyIndex = 0;

        //[Inject] private NavigationManager navigationManager { get; set; }
        [Inject] private IJSRuntime _JSRuntime { get; set; }

        protected async override Task OnInitializedAsync()
        {
            //Console.WriteLine(navigationManager.BaseUri);

            var refs = AppDomain.CurrentDomain.GetAssemblies();
            var client = new HttpClient
            {
                BaseAddress = new Uri(navigationManager.BaseUri)
            };

            var references = new List<MetadataReference>();

            foreach (var reference in refs.Where(x => !x.IsDynamic && !string.IsNullOrWhiteSpace(x.Location)))
            {
                Console.WriteLine(reference.Location);
                var stream = await client.GetStreamAsync($"_framework/_bin/{reference.Location}");
                references.Add(MetadataReference.CreateFromStream(stream));
            }
            Disabled = false;
            _references = references;
        }

        protected override Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                //Console.WriteLine("OnAfterRenderAsync");
                //IJSInProcessRuntime Runtime = _JSRuntime as IJSInProcessRuntime;
                //Runtime.Invoke<object>("exampleJsFunctions.focusElement", "input");
                //Console.WriteLine("OnAfterRenderAsync");
                //inputElem
            }

            return base.OnAfterRenderAsync(firstRender);
        }

        public void OnKeyDown(KeyboardEventArgs e)
        {
            Console.WriteLine("OnKeyDown:" + e.Key);

            if (e.Key == "ArrowUp" && _historyIndex > 0)
            {
                _historyIndex--;
                Input = _history[_historyIndex];
            }
            else if (e.Key == "ArrowDown" && _historyIndex + 1 < _history.Count)
            {
                _historyIndex++;
                Input = _history[_historyIndex];
            }
            // todo:  doesn't work right when typing new command.  Requires Enter to be pressed first sometimes
            // has to do with DOM focus I think
            // currently handling this with javascript as well
            else if (e.Key == "Escape")
            {
                Input = "";
                _historyIndex = _history.Count;
            }
        }

        public async Task Run(KeyboardEventArgs e)
        {
            Console.WriteLine("Run:" + e.Key);

            if (e.Key != "Enter")
            {
                return;
            }

            Console.WriteLine("Run:" + e.Key);

            var code = Input;
            if (!string.IsNullOrEmpty(code))
            {
                _history.Add(code);
            }
            _historyIndex = _history.Count;
            Input = "";
            
            //StateHasChanged();

            await RunSubmission(code);
        }

        private async Task RunSubmission(string code)
        {
            Console.WriteLine("RunSubmission:" + code);

            Output += $@"<br /><span class=""info"">{HttpUtility.HtmlEncode(code)}</span>";

            var previousOut = Console.Out;
            try
            {
                if (TryCompile(code, out var script, out var errorDiagnostics))
                {
                    var writer = new StringWriter();
                    Console.SetOut(writer);

                    var entryPoint = _previousCompilation.GetEntryPoint(CancellationToken.None);
                    var type = script.GetType($"{entryPoint.ContainingNamespace.MetadataName}.{entryPoint.ContainingType.MetadataName}");
                    var entryPointMethod = type.GetMethod(entryPoint.MetadataName);

                    var submission = (Func<object[], Task>)entryPointMethod.CreateDelegate(typeof(Func<object[], Task>));

                    if (_submissionIndex >= _submissionStates.Length)
                    {
                        Array.Resize(ref _submissionStates, Math.Max(_submissionIndex, _submissionStates.Length * 2));
                    }

                    var returnValue = await ((Task<object>)submission(_submissionStates));
                    if (returnValue != null)
                    {
                        Console.WriteLine(CSharpObjectFormatter.Instance.FormatObject(returnValue));
                    }

                    var output = HttpUtility.HtmlEncode(writer.ToString());
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Output += $"<br />{output}";
                    }
                }
                else
                {
                    foreach (var diag in errorDiagnostics)
                    {
                        Output += $@"<br / ><span class=""error"">{HttpUtility.HtmlEncode(diag)}</span>";
                    }
                }
            }
            catch (Exception ex)
            {
                Output += $@"<br /><span class=""error"">{HttpUtility.HtmlEncode(CSharpObjectFormatter.Instance.FormatException(ex))}</span>";
            }
            finally
            {
                Console.SetOut(previousOut);
            }

            //StateHasChanged();
        }

        private bool TryCompile(string source, out Assembly assembly, out IEnumerable<Diagnostic> errorDiagnostics)
        {
            assembly = null;
            var scriptCompilation = CSharpCompilation.CreateScriptCompilation(
                Path.GetRandomFileName(),
                CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithKind(SourceCodeKind.Script).WithLanguageVersion(LanguageVersion.Preview)),
                _references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: new[]
                {
                    "System",
                    "System.IO",
                    "System.Collections.Generic",
                    "System.Console",
                    "System.Diagnostics",
                    "System.Dynamic",
                    "System.Linq",
                    "System.Linq.Expressions",
                    "System.Net.Http",
                    "System.Text",
                    "System.Threading.Tasks"
                        }),
                _previousCompilation
            );

            errorDiagnostics = scriptCompilation.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error);
            if (errorDiagnostics.Any())
            {
                return false;
            }

            using (var peStream = new MemoryStream())
            {
                var emitResult = scriptCompilation.Emit(peStream);

                if (emitResult.Success)
                {
                    _submissionIndex++;
                    _previousCompilation = scriptCompilation;
                    assembly = Assembly.Load(peStream.ToArray());
                    return true;
                }
            }

            return false;
        }
    }
}
