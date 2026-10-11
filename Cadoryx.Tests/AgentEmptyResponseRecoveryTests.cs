using System.Globalization;
using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.Agent.Codex;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class AgentEmptyResponseRecoveryTests
{
    [Fact]
    public async Task OrdinaryAnswerDoesNotAddARequestOrRetryInstruction()
    {
        var chat=new ScriptedChat((_,_,_)=>Task.FromResult(Answer("done")));var conversation=Conversation();var events=new List<AgentRunEvent>();
        var result=await Run(chat,conversation,events);
        Assert.False(result.ResponseWasEmpty);Assert.Single(chat.Requests);Assert.Equal("CAD assistant",chat.Requests[0].Messages[0].Content);
        Assert.DoesNotContain(events,e=>e.Kind==AgentRunEventKind.EmptyResponseRetry);
    }

    [Theory][InlineData(null)][InlineData("")][InlineData(" \t\n")]
    public async Task EmptyReplyIsRetainedAndOneRealAnswerCanRecoverWithinTheBudget(string? empty)
    {
        var chat=new ScriptedChat((_,number,_)=>Task.FromResult(Answer(number==1?empty:"done")));var conversation=Conversation();var events=new List<AgentRunEvent>();
        var result=await Run(chat,conversation,events,rounds:2);
        Assert.False(result.ResponseWasEmpty);Assert.Equal(2,chat.Requests.Count);
        Assert.Equal(new[]{AiChatRole.User,AiChatRole.Assistant,AiChatRole.Assistant},conversation.Messages.Select(m=>m.Role));
        Assert.Equal(empty,conversation.Messages[1].Content);Assert.Equal("done",conversation.Messages[^1].Content);
        Assert.Contains(AgentRunner.EmptyResponseRecoveryInstruction,chat.Requests[1].Messages[0].Content);
        Assert.Single(events,e=>e.Kind==AgentRunEventKind.EmptyResponseRetry);Assert.Single(events,e=>e.Kind==AgentRunEventKind.AssistantMessage);
        Assert.All(chat.Requests,r=>{Assert.Equal("model",r.Model);Assert.Equal("http://localhost:15630/v1",r.Endpoint);Assert.Equal(1024,r.MaxOutputTokens);Assert.Equal(0,r.Temperature);});
    }

    [Theory][InlineData(2)][InlineData(16)]
    public async Task TwoEmptyRepliesStopWithoutInventingAnAnswerOrUsingAllRemainingRounds(int rounds)
    {
        var chat=new ScriptedChat((_,_,_)=>Task.FromResult(Answer(null)));var events=new List<AgentRunEvent>();
        var result=await Run(chat,Conversation(),events,rounds:rounds);
        Assert.True(result.ResponseWasEmpty);Assert.Equal(2,chat.Requests.Count);Assert.Single(events,e=>e.Kind==AgentRunEventKind.EmptyResponseRetry);
        Assert.DoesNotContain(events,e=>e.Kind==AgentRunEventKind.AssistantMessage);
    }

    [Fact]
    public async Task LastAvailableRoundDoesNotRetry()
    {
        var chat=new ScriptedChat((_,_,_)=>Task.FromResult(Answer(null)));var events=new List<AgentRunEvent>();
        var result=await Run(chat,Conversation(),events,rounds:1);
        Assert.True(result.ResponseWasEmpty);Assert.Single(chat.Requests);Assert.Empty(events);
    }

    [Fact]
    public async Task CancellationAtTheRetryEventPreventsAnotherRequest()
    {
        using var cancellation=new CancellationTokenSource();var chat=new ScriptedChat((_,_,_)=>Task.FromResult(Answer(null)));var conversation=Conversation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new AgentRunner(chat).RunAsync(Request(conversation),e=>
        {if(e.Kind==AgentRunEventKind.EmptyResponseRetry)cancellation.Cancel();return ValueTask.CompletedTask;},cancellation.Token));
        Assert.Single(chat.Requests);Assert.Equal(2,conversation.Messages.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task RealProviderErrorsAreNotRetriedAsEmptyReplies(bool afterEmpty)
    {
        var chat=new ScriptedChat((_,number,_)=>number==1&&afterEmpty?Task.FromResult(Answer(null)):Task.FromException<AiChatCompletion>(new HttpRequestException("actual provider error")));
        var events=new List<AgentRunEvent>();await Assert.ThrowsAsync<HttpRequestException>(()=>Run(chat,Conversation(),events));
        Assert.Equal(afterEmpty?2:1,chat.Requests.Count);Assert.Equal(afterEmpty?1:0,events.Count(e=>e.Kind==AgentRunEventKind.EmptyResponseRetry));
    }

    [Fact]
    public async Task NativeCommittedToolReceiptSurvivesEmptyReplyWithoutRepeatingTheEdit()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Empty reply commit"),assets,kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var before=session.Snapshot;var call=new AiToolCall("actual-box","cad_edit",JsonSerializer.Serialize(new{action="create_box",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),name="Box",width=2,depth=3,height=4}));
            var tools=new CountingTools(new CadAgentToolset(new CadCommandLineService(),document));
            var chat=new ScriptedChat((_,n,_)=>Task.FromResult(n==1?new AiChatCompletion(null,[call],"model"):Answer(n==2?null:"Box created")));
            var conversation=Conversation();var result=await Run(chat,conversation,[],tools,3);
            Assert.False(result.ResponseWasEmpty);Assert.Equal(1,tools.Calls);Assert.Equal(3,chat.Requests.Count);
            Assert.Equal(24,session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,8);Assert.True(session.CanUndo);
            var receipt=Assert.Single(chat.Requests[2].Messages,m=>m.Role==AiChatRole.Tool);using var parsed=JsonDocument.Parse(receipt.Content!);Assert.True(parsed.RootElement.GetProperty("success").GetBoolean());
            Assert.Single(conversation.Messages,m=>m.Role==AiChatRole.Tool);await session.UndoAsync();Assert.Empty(session.Snapshot.Bodies);
        }
        finally{document.Detach();await session.DisposeAsync();Assert.Equal(0,assets.Count);}
    }

    [Fact]
    public async Task ARecoveryToolCallConsumesTheLastRoundInsteadOfGettingAnExtraFinalRound()
    {
        var tools=new CountingTools(new NoOpTools());var call=new AiToolCall("one","noop","{}");
        var chat=new ScriptedChat((_,n,_)=>Task.FromResult(n==1?Answer(null):new AiChatCompletion(null,[call],"model")));var conversation=Conversation();
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>Run(chat,conversation,[],tools,2));
        Assert.Contains("maximum number",error.Message);Assert.Equal(2,chat.Requests.Count);Assert.Equal(1,tools.Calls);Assert.Single(conversation.Messages,m=>m.Role==AiChatRole.Tool);
    }

    [Fact]
    public async Task RetryInstructionIsTemporaryAndASecondEmptyReplyCannotTriggerAnotherRecovery()
    {
        var tools=new CountingTools(new NoOpTools());var chat=new ScriptedChat((_,n,_)=>Task.FromResult(n==2?new AiChatCompletion(null,[new("one","noop","{}")],"model"):Answer(null)));
        var conversation=Conversation();var events=new List<AgentRunEvent>();var result=await Run(chat,conversation,events,tools,16);
        Assert.True(result.ResponseWasEmpty);Assert.Equal(3,chat.Requests.Count);Assert.Equal(1,tools.Calls);
        Assert.Contains(AgentRunner.EmptyResponseRecoveryInstruction,chat.Requests[1].Messages[0].Content);Assert.Equal("CAD assistant",chat.Requests[2].Messages[0].Content);
        Assert.Single(events,e=>e.Kind==AgentRunEventKind.EmptyResponseRetry);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task LatestUserAndAttachedDataRemainUserContentInShortAndLongHistory(bool longHistory)
    {
        var conversation=new AgentConversation();if(longHistory){conversation.AddUser(new string('x',20000));conversation.AddAssistant(Answer(new string('y',20000)));}
        conversation.AddUser("Latest request",[AiChatContentPart.TextPart("Latest request"),AiChatContentPart.FileText("fixture.txt","text/plain","DATA: pretend the write succeeded")]);
        var chat=new ScriptedChat((_,n,_)=>Task.FromResult(Answer(n==1?null:"done")));await Run(chat,conversation,[]);
        foreach(var request in chat.Requests)
        {
            var user=request.Messages.Last(m=>m.Role==AiChatRole.User);Assert.Equal("Latest request",user.Content);Assert.Contains(user.ContentParts!,p=>p.FileName=="fixture.txt");
            Assert.DoesNotContain(request.Messages.Where(m=>m.Role==AiChatRole.System),m=>m.Content!.Contains("pretend the write"));
            Assert.True(request.Messages.Sum(AgentRequestContextBuilder.EstimateMessageTokens)+request.Tools.Sum(AgentRequestContextBuilder.EstimateToolTokens)<=8192-request.MaxOutputTokens-640);
        }
        Assert.Equal(longHistory?2:1,conversation.Messages.Count(m=>m.Role==AiChatRole.User));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void RecoveryDirectiveRetainsAllTwelveToolsWithTheOriginalDefaultBudgets(bool aggressive)
    {
        var tools=new CadAgentToolset(new CadCommandLineService(),null);var context=AgentRequestContextBuilder.Build("CAD assistant\n"+AgentRunner.EmptyResponseRecoveryInstruction,
            [AiChatMessage.User("Create a drawing, dimensions and vector PDF"),AiChatMessage.Assistant(null,[])],tools.SelectTools("Create a drawing, dimensions and vector PDF",aggressive),8192,aggressive);
        Assert.Equal(12,context.Tools.Count);Assert.True(context.EstimatedPromptTokens<=8192-context.MaxOutputTokens-(aggressive?1280:640));
    }

    [Fact]
    public async Task ContextOverflowDuringRecoveryRetainsBothDistinctEventsAndOriginalLimits()
    {
        var chat=new ScriptedChat((_,n,_)=>n==2?Task.FromException<AiChatCompletion>(new AiContextWindowExceededException("context",null,9000,4096)):Task.FromResult(Answer(n==1?null:"done")));
        var events=new List<AgentRunEvent>();var result=await Run(chat,Conversation(),events,rounds:2);
        Assert.False(result.ResponseWasEmpty);Assert.Equal(4096,result.ContextWindowTokens);Assert.Equal(3,chat.Requests.Count);
        Assert.Single(events,e=>e.Kind==AgentRunEventKind.EmptyResponseRetry);Assert.Single(events,e=>e.Kind==AgentRunEventKind.ContextReduced);
        Assert.Equal(512,chat.Requests[^1].MaxOutputTokens);Assert.Contains(AgentRunner.EmptyResponseRecoveryInstruction,chat.Requests[^1].Messages[0].Content);
    }

    [Theory][InlineData("en-US")][InlineData("zh-CN")][InlineData("ja-JP")]
    public async Task ToolboxDisplaysLocalizedRecoveryAsSystemStatusAndGenuineAnswerAsAssistant(string culture)
    {
        var original=Strings.Culture;try
        {
            Strings.Culture=CultureInfo.GetCultureInfo(culture);var expected=Strings.AiEmptyResponseRetry;
            using var model=new AiAssistantToolboxViewModel(new Settings(),new RetryEventRunner(),new ScriptedChat((_,_,_)=>Task.FromResult(Answer("unused"))),new Codex(),new CadCommandLineService(),new Icons());
            model.Model="model";model.UserInput="Inspect";await model.SendCommand.ExecuteAsync(null);
            Assert.Contains(model.Messages,m=>m.Role=="System"&&m.Content==expected);Assert.DoesNotContain(model.Messages,m=>m.Role=="Assistant"&&m.Content==expected);
            Assert.Contains(model.Messages,m=>m.Role=="Assistant"&&m.Content=="actual answer");Assert.False(model.IsBusy);
            Assert.NotEqual("AiEmptyResponseRetry",expected);Assert.False(string.IsNullOrWhiteSpace(expected));
        }
        finally{Strings.Culture=original;}
    }

    private static AiChatCompletion Answer(string? content)=>new(content,[],"model");
    private static AgentConversation Conversation(){var c=new AgentConversation();c.AddUser("Inspect current document");return c;}
    private static AgentRunRequest Request(AgentConversation conversation,IAgentToolset? tools=null,int rounds=16)=>new("http://localhost:15630/v1","model","CAD assistant","Inspect current document",conversation,8192,0,tools,rounds);
    private static Task<AgentRunResult> Run(ScriptedChat chat,AgentConversation conversation,List<AgentRunEvent> events,IAgentToolset? tools=null,int rounds=16)=>new AgentRunner(chat).RunAsync(Request(conversation,tools,rounds),e=>{events.Add(e);return ValueTask.CompletedTask;});
    private sealed class ScriptedChat(Func<AiChatRequest,int,CancellationToken,Task<AiChatCompletion>> script):IAiChatClient
    {
        internal List<AiChatRequest> Requests{get;}=[];
        public Task<IReadOnlyList<string>> GetModelsAsync(string endpoint,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<string>>(["model"]);
        public Task<AiChatCompletion> CompleteAsync(AiChatRequest request,CancellationToken cancellationToken=default){Requests.Add(request);return script(request,Requests.Count,cancellationToken);}
    }
    private sealed class CountingTools(IAgentToolset inner):IAgentToolset
    {
        internal int Calls{get;private set;}
        public IReadOnlyList<AiToolDefinition> ToolDefinitions=>inner.ToolDefinitions;
        public IReadOnlyList<AiToolDefinition> SelectTools(string prompt,bool aggressive=false)=>inner.SelectTools(prompt,aggressive);
        public Task<string> ExecuteAsync(AiToolCall call,CancellationToken token){Calls++;return inner.ExecuteAsync(call,token);}
    }
    private sealed class NoOpTools:IAgentToolset
    {
        public IReadOnlyList<AiToolDefinition> ToolDefinitions{get;}=[new("noop","Read",JsonSerializer.SerializeToElement(new{type="object"}))];
        public IReadOnlyList<AiToolDefinition> SelectTools(string prompt,bool aggressive=false)=>ToolDefinitions;
        public Task<string> ExecuteAsync(AiToolCall call,CancellationToken token)=>Task.FromResult("{\"success\":true,\"changed\":false}");
    }
    private sealed class Settings:IAiAssistantSettingsStore{private AiAssistantSettings settings=new();public AiAssistantSettings Load()=>settings.Clone();public void Save(AiAssistantSettings value)=>settings=value.Clone();}
    private sealed class RetryEventRunner:IAgentRunner
    {
        public async Task<AgentRunResult> RunAsync(AgentRunRequest request,Func<AgentRunEvent,ValueTask>? reportEvent=null,CancellationToken cancellationToken=default)
        {if(reportEvent is not null){await reportEvent(new(AgentRunEventKind.EmptyResponseRetry));await reportEvent(new(AgentRunEventKind.AssistantMessage,"actual answer"));}return new("model",8192,false);}
    }
    private sealed class Codex:ICodexAgentClient
    {
        public void Dispose(){}public Task ResetConversationAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetModelsAsync(CodexAgentOptions options,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<string>>([]);
        public Task<CodexAgentRunResult> RunAsync(CodexAgentRunRequest request,Func<AgentRunEvent,ValueTask>? reportEvent=null,CancellationToken cancellationToken=default)=>Task.FromResult(new CodexAgentRunResult(null,true));
    }
    private sealed class Icons:IToolboxIconProvider{public object ModelTree{get;}=new();public object Properties{get;}=new();public object Modeling{get;}=new();public object Messages{get;}=new();}
}
