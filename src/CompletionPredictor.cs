using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Subsystem.Prediction;

namespace Microsoft.PowerShell.Predictor;

using System.Management.Automation;

public partial class CompletionPredictor : ICommandPredictor, IDisposable
{
    private readonly Guid _guid;
    private readonly Runspace _runspace;
    private int _lock = 1;

    private static HashSet<string> s_cmdList = new(StringComparer.OrdinalIgnoreCase)
    {
        "%",
        "foreach",
        "ForEach-Object",
        "?",
        "where",
        "Where-Object"
    };

    internal CompletionPredictor(string guid)
    {
        _guid = new Guid(guid);
        _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
        _runspace.Open();

        PopulateInitialState();
        RegisterEvents();
    }

    public Guid Id => _guid;
    public string Name => "Completion";
    public string Description => "Predictive intellisense based on PowerShell completion.";

    public SuggestionPackage GetSuggestion(PredictionClient client, PredictionContext context, CancellationToken cancellationToken)
    {
        Token tokenAtCursor = context.TokenAtCursor;
        IReadOnlyList<Ast> relatedAsts = context.RelatedAsts;

        if (tokenAtCursor is null)
        {
            // When it ends at a white space, it would likely trigger argument completion which in most cases would be file-operation
            // intensive. That's not only slow but also undesirable in most cases, so we skip it.
            // But, there are exceptions for 'ForEach-Object' and 'Where-Object', where completion on member names is quite useful.
            Ast lastAst = relatedAsts[^1];
            var cmdName = (lastAst.Parent as CommandAst)?.CommandElements[0] as StringConstantExpressionAst;
            if (cmdName is null || !s_cmdList.Contains(cmdName.Value) || !object.ReferenceEquals(lastAst, cmdName))
            {
                // So we stop processing unless the cursor is right after 'ForEach-Object' or 'Where-Object'.
                return default;
            }
        }

        if (tokenAtCursor is not null && tokenAtCursor.TokenFlags.HasFlag(TokenFlags.CommandName))
        {
            // When it's a command, it would likely take too much time because the command discovery is usually expensive, so we skip it.
            return default;
        }

        // Call into PowerShell tab completion to get completion results.
        // The runspace may be held by another call, or the call may take too long and exceed the timeout.
        CommandCompletion? result = GetCompletionResults(context.InputAst, context.InputTokens, context.CursorPosition);
        if (result is null || cancellationToken.IsCancellationRequested)
        {
            return default;
        }

        int count = result.CompletionMatches.Count;
        if (count > 0)
        {
            count = count > 10 ? 10 : count;
            var list = new List<PredictiveSuggestion>(count);

            string input = context.InputAst.Extent.Text;
            var head = result.ReplacementIndex == 0 ? ReadOnlySpan<char>.Empty : input.AsSpan(0, result.ReplacementIndex);

            for (int i = 0; i < count; i++)
            {
                var completion = result.CompletionMatches[i];
                list.Add(new PredictiveSuggestion(string.Concat(head, completion.CompletionText), completion.ToolTip));
            }

            return new SuggestionPackage(list);
        }

        return default;
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

    public bool CanAcceptFeedback(PredictionClient client, PredictorFeedbackKind feedback) => false;
    public void OnSuggestionDisplayed(PredictionClient client, uint session, int countOrIndex) { }
    public void OnSuggestionAccepted(PredictionClient client, uint session, string acceptedSuggestion) { }
    public void OnCommandLineAccepted(PredictionClient client, IReadOnlyList<string> history) { }
    public void OnCommandLineExecuted(PredictionClient client, string commandLine, bool success) { }

    #endregion;
}