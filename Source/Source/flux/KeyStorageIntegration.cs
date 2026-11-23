using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace KaidenFlux
{
    internal sealed partial class FluxShell
    {
        private SecureApiKeyStore savedKey;
        private TextBlock keyStatus;
        private DispatcherTimer keySaveTimer;
        private bool keyChanged;
        private void InitializeKeyStorage()
        {
            var box=C<PasswordBox>("ApiKey");var parent=(StackPanel)box.Parent;
            keyStatus=StudioUi.Text(preview?"Key saving is disabled in preview.":"Keys are encrypted for your Windows account.",11);parent.Children.Insert(parent.Children.IndexOf(box)+1,keyStatus);
            var forget=StudioUi.Button(Window,"Forget saved key",()=>{if(preview)return;try{box.Clear();savedKey.Forget();keyChanged=false;keySaveTimer.Stop();keyStatus.Text="Saved key removed.";}catch(Exception){keyStatus.Text="Could not remove the saved key. Check folder permissions.";}},Status);parent.Children.Insert(parent.Children.IndexOf(keyStatus)+1,forget);
            if(preview){forget.IsEnabled=false;return;}
            savedKey=new SecureApiKeyStore(Path.Combine(FluxState.Folder,"gemini-key.dat"));var existing=savedKey.Read();if(existing!=null){box.Password=existing;keyStatus.Text="Saved key loaded · Windows account encryption.";}else if(savedKey.Warning!=null)keyStatus.Text=savedKey.Warning;
            keySaveTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(650)};keySaveTimer.Tick+=(s,e)=>SaveKey();
            box.PasswordChanged+=(s,e)=>{keyChanged=true;keySaveTimer.Stop();keySaveTimer.Start();keyStatus.Text="Saving key securely…";};
            Window.Closed+=(s,e)=>{keySaveTimer.Stop();if(keyChanged)SaveKey();};
        }
        private void SaveKey()
        {
            keySaveTimer.Stop();if(!keyChanged)return;
            try{savedKey.Save(C<PasswordBox>("ApiKey").Password);keyChanged=false;keyStatus.Text=C<PasswordBox>("ApiKey").Password.Length==0?"No key saved.":"Key saved · encrypted for this Windows account.";}
            catch(Exception){keyStatus.Text="Key could not be saved. It remains available for this session.";}
        }
    }
}
