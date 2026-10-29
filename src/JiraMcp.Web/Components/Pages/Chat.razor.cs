using JiraMcp.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace JiraMcp.Web.Components.Pages;

public partial class Chat
{
    [Inject] private ChatSession Session { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private string _input = "";
    private IJSObjectReference? _module;

    protected override void OnInitialized() => Session.Changed += OnSessionChanged;

    private void OnSessionChanged() => _ = InvokeAsync(StateHasChanged);

    private async Task SendAsync()
    {
        var text = _input;
        _input = "";
        await Session.SendAsync(text);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Chat.razor.js");
        await _module.InvokeVoidAsync("scrollToEnd");
    }

    public void Dispose() => Session.Changed -= OnSessionChanged;
}
