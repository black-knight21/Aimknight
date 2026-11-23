using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
namespace KaidenFlux
{
    internal sealed partial class FluxShell
    {
        private LocalTuningMemory localMemory;
        private TextBlock memoryStatus;
        private Button memoryToggle;
        private string pendingAiRequest;
        private void InitializeMemory()
        {
            localMemory=new LocalTuningMemory(preview?null:Path.Combine(FluxState.Folder,"ai-memory.json"));
            var panel=new StackPanel();panel.Children.Add(StudioUi.Text("Local tuning history",15));
            memoryStatus=StudioUi.Text("",11);panel.Children.Add(memoryStatus);
            panel.Children.Add(StudioUi.Text("Recent requests and results are stored on this PC. Up to five summaries are sent with a Gemini request. API keys are excluded from history.",11));
            memoryToggle=StudioUi.Button(Window,"Memory",()=>MemoryAction(()=>localMemory.SetEnabled(!localMemory.Enabled)),Status);panel.Children.Add(memoryToggle);
            panel.Children.Add(StudioUi.Button(Window,"Clear local history",()=>MemoryAction(()=>{localMemory.Clear();recentRequests.Clear();pendingAiRequest=null;C<StackPanel>("Messages").Children.Clear();Message("LOCAL HISTORY","History cleared on this PC.",false);}),Status));
            var applyPanel=(StackPanel)C<Button>("ApplyAI").Parent;applyPanel.Children.Add(new Expander {Header="Local memory",Content=panel,Foreground=Brush("#AAA6B2"),Margin=new Thickness(0,12,0,0)});
            if(localMemory.Enabled)foreach(var entry in localMemory.Entries.Reverse().Take(3).Reverse()) {
                string request=(string)entry["request"];if(!string.IsNullOrWhiteSpace(request)){Message("SAVED REQUEST",request,true);}
                string response=(string)entry["response"];if(!string.IsNullOrWhiteSpace(response))Message("SAVED RESULT · "+(string)entry["event"],response,false);
            }
            UpdateMemoryStatus();
        }
        private void UpdateMemoryStatus(){if(localMemory==null)return;memoryToggle.Content=localMemory.Enabled?"Memory: On":"Memory: Off";memoryStatus.Text=localMemory.Warning??(localMemory.Entries.Count+" entries stored locally. "+(localMemory.Enabled?"Recent context enabled.":"History is not sent to Gemini."));}
        private void MemoryAction(Action action){try{action();UpdateMemoryStatus();}catch(Exception ex){memoryStatus.Text="Local memory unavailable: "+ex.Message;}}
        private void Remember(string kind,string request,string response,string changes)
        {
            if(localMemory==null||preview)return;MemoryAction(()=>localMemory.Add(kind,request,response,changes,C<PasswordBox>("ApiKey").Password));
        }
    }
}
