using System.Diagnostics.CodeAnalysis;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Subsystem.Prediction;

namespace Microsoft.PowerShell.Predictor;

using System.Management.Automation;

public partial class CompletionPredictor : ICommandPredictor, IDisposable
{
    private readonly Guid _guid;
    private readonly Runspace _runspace;
    private string? _cwd;
    private int _lock = 1;

    internal CompletionPredictor(string guid)
    {
        _guid = new Guid(guid);
        _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
        _runspace.Name = nameof(CompletionPredictor);
        _runspace.Open();

        PopulateInitialState();
        RegisterEvents();
    }

    public Guid Id => _guid;
    public string Name => "Completion";
    public string Description => "Predictive intellisense based on PowerShell completion.";

    public SuggestionPackage GetSuggestion(PredictionClient client, PredictionContext context, CancellationToken cancellationToken)
    {
        return GetFromTabCompletion(context, cancellationToken);
    }

    private static bool IsCommandAstWithLiteralName(
        PredictionContext context,
        [NotNullWhen(true)] out CommandAst? cmdAst,
        [NotNullWhen(true)] out StringConstantExpressionAst? nameAst)
    {
        Ast lastAst = context.RelatedAsts[^1];
        cmdAst = lastAst.Parent as CommandAst;
        nameAst = cmdAst?.CommandElements[0] as StringConstantExpressionAst;
        return nameAst is not null;
    }

    private SuggestionPackage GetFromTabCompletion(PredictionContext context, CancellationToken cancellationToken)
    {
        // Call into PowerShell tab completion to get completion results.
        // The runspace may be held by another call, or the call may take too long and exceed the timeout.
        CommandCompletion? result = GetCompletionResults(context.InputAst, context.InputTokens, context.CursorPosition);
        if (result is null || result.CompletionMatches.Count == 0 || cancellationToken.IsCancellationRequested)
        {
            return default;
        }

        int count = result.CompletionMatches.Count > 30 ? 30 : result.CompletionMatches.Count;
        List<PredictiveSuggestion>? list = null;

        int replaceIndex = result.ReplacementIndex;
        string input = context.InputAst.Extent.Text;

        ReadOnlySpan<char> head = replaceIndex == 0 ? ReadOnlySpan<char>.Empty : input.AsSpan(0, replaceIndex);
        ReadOnlySpan<char> diff = input.AsSpan(replaceIndex);

        for (int i = 0; i < count; i++)
        {
            CompletionResult completion = result.CompletionMatches[i];
            ReadOnlySpan<char> text = completion.CompletionText.AsSpan();
            string? tooltip = completion.CompletionText == completion.ToolTip ? null : completion.ToolTip;
            string? suggestion = null;

            switch (completion.ResultType)
            {
                case CompletionResultType.ProviderItem or CompletionResultType.ProviderContainer when !diff.IsEmpty:
                    // For local paths, if the input doesn't contain the prefix, then we stripe it from the suggestion.
                    bool removeLocalPathPrefix =
                        text.IndexOf(diff, StringComparison.OrdinalIgnoreCase) == 2 &&
                        text[0] == '.' && text[1] == Path.DirectorySeparatorChar;
                    ReadOnlySpan<char> newPart = removeLocalPathPrefix ? text.Slice(2) : text;
                    suggestion = string.Concat(head, newPart);

                    break;

                default:
                    break;
            }

            suggestion ??= string.Concat(head, text);
            if (!string.Equals(input, suggestion, StringComparison.OrdinalIgnoreCase))
            {
                list ??= new List<PredictiveSuggestion>(count);
                list.Add(new PredictiveSuggestion(suggestion, tooltip));
            }
        }

        return list is null ? default : new SuggestionPackage(list);
    }

    private CommandCompletion? GetCompletionResults(Ast inputAst, IReadOnlyCollection<Token> inputTokens, IScriptPosition cursorPosition)
    {
        // A simple way that denies reentrancy. We need this because this method could be called in parallel
        // (each keystroke triggers a call to this method), but the Runspace cannot handle that. So, if the
        // Runspace is busy, the call is ignored.
        // Value 1 indicates the runspace is available for use.
        if (Interlocked.Exchange(ref _lock, 0) != 1)
        {
            return null;
        }

        Runspace oldRunspace = Runspace.DefaultRunspace;

        try
        {
            Runspace.DefaultRunspace = _runspace;
            Token[] tokens = (Token[])inputTokens ?? inputTokens!.ToArray();
            return CommandCompletion.CompleteInput(inputAst, tokens, cursorPosition, options: null);
        }
        finally
        {
            Interlocked.Exchange(ref _lock, 1);
            Runspace.DefaultRunspace = oldRunspace;
        }
    }

    /// <summary>
    /// This will be called when the module is being unloaded, from the pipeline thread.
    /// </summary>
    public void Dispose()
    {
        UnregisterEvents();
        _runspace.Dispose();
    }

    #region "Unused interface members because this predictor doesn't process feedback"

    public bool CanAcceptFeedback(PredictionClient client, PredictorFeedbackKind feedback)
    {
        return feedback == PredictorFeedbackKind.CommandLineAccepted ? true : false;
    }

    public void OnSuggestionDisplayed(PredictionClient client, uint session, int countOrIndex) { }
    public void OnSuggestionAccepted(PredictionClient client, uint session, string acceptedSuggestion) { }
    public void OnCommandLineExecuted(PredictionClient client, string commandLine, bool success) { }
    public void OnCommandLineAccepted(PredictionClient client, IReadOnlyList<string> history) { }

    #endregion;
}
