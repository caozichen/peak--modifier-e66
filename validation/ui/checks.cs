using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PeakWorkshop.Desktop;
using PeakWorkshop.Protocol;

internal static class Checks
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static object Field(object owner, string name) => owner.GetType().GetField(name, All).GetValue(owner);
    static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, All).SetValue(owner, value);
    static void Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, All).Invoke(window, args);
    static object[] Cards(MainWindow window) => ((IDictionary)Field(window, "_cards")).Values.Cast<object>().ToArray();
    static object[] Groups(MainWindow window) => ((IList)Field(window, "_groups")).Cast<object>().ToArray();
    static ScrollViewer Viewport(MainWindow window) => (ScrollViewer)window.FindName("FeatureViewport");
    static Button ClearButton(MainWindow window) => (Button)window.FindName("ClearStatusButton");
    static string SelectedGroup(MainWindow window) => (string)Field(window, "_selectedGroup");
    static Button Navigation(MainWindow window, string group) => (Button)((IDictionary)Field(window, "_navigation"))[group];
    static double GroupTop(MainWindow window, object group) => ((FrameworkElement)Field(group,"Container")).TranslatePoint(new Point(),(UIElement)Viewport(window).Content).Y;
    static void Layout(MainWindow window, double width=1180, double height=860)
    {
        // Measure/arrange the content without an HWND or Loaded event. This exercises
        // real WPF templates and ScrollChanged while keeping the IPC timer stopped.
        var content=(FrameworkElement)window.Content;
        content.Measure(new Size(width,height));
        content.Arrange(new Rect(0,0,width,height));
        content.UpdateLayout();
        content.UpdateLayout();
    }
    static void Scroll(MainWindow window, double offset)
    {
        Viewport(window).ScrollToVerticalOffset(offset);
        Layout(window);
    }
    static void ClickNavigation(MainWindow window, string group)
    {
        Navigation(window,group).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(window);
    }
    static void AssertAllVisible(MainWindow window)
    {
        Assert(Cards(window).Length==51 && Cards(window).All(c=>((FrameworkElement)Field(c,"Container")).Visibility==Visibility.Visible),"Menu hid feature cards");
        Assert(Groups(window).All(g=>((FrameworkElement)Field(g,"Container")).Visibility==Visibility.Visible),"Menu hid groups");
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static WireMessage State(bool ready)
    {
        return new WireMessage { ok=true, sessionId="ui-fixture", pluginVersion="0.2.4", scene="Airport",
            characterReady=ready, characterReadinessMessage=ready ? "本地角色就绪。" : "角色组件尚未完成初始化。",
            clearStatusAvailable=true, detachAvailable=true, features=FeatureCatalog.All.Select(f=>new FeatureValue {id=f.Id, value=f.Default, available=true}).ToArray() };
    }
    static bool InputsEnabled(object card, bool expected)
    {
        return new[]{"Toggle","Slider","Editor","Apply"}.Select(n=>Field(card,n)).Where(v=>v!=null)
            .Cast<System.Windows.UIElement>().All(v=>v.IsEnabled==expected);
    }
    [STAThread] static int Main(string[] args)
    {
        var app=new App();app.InitializeComponent();
        var window=new MainWindow(); // Never shown: no Loaded event, timer, or game connection.
        var results=new List<object>();int failed=0;
        void Check(string name, Action body)
        {
            try { body();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name); }
            catch(Exception ex) { failed++;results.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+": "+ex); }
        }
        Check("Portable main window has a game management entry and no developer game path",()=>{
            Assert(window.Title.Contains("0.3.2")&&window.Title.Contains("这次我一定要爬到顶"),"Wrong product title");
            Assert(window.FindName("SetupButton") is Button,"Game manager missing");
            Assert(!((TextBlock)window.FindName("GamePathText")).Text.Contains("SteamLibrary"),"Hardcoded developer game path");
        });
        Check("Portable setup exposes discovery, manual selection and explicit install actions",()=>{
            var setup=new SetupWindow("");
            foreach(string name in new[]{"GameSelector","FindButton","BrowseButton","InstallButton","UninstallButton","CollectButton","DoneButton"})Assert(setup.FindName(name)!=null,"Setup action missing: "+name);
            Assert(!((Button)setup.FindName("InstallButton")).IsEnabled&&!((Button)setup.FindName("UninstallButton")).IsEnabled,"Fresh manager allows uninspected mutation");
        });
        Check("In-flight setup disables navigation and cancels closing until transaction settles",()=>{
            var setup=new SetupWindow("");typeof(SetupWindow).GetMethod("SetWorking",All).Invoke(setup,new object[]{true});
            foreach(string name in new[]{"GameSelector","FindButton","BrowseButton","InstallButton","UninstallButton","CollectButton","DoneButton"})Assert(!((UIElement)setup.FindName(name)).IsEnabled,"Busy setup left action enabled: "+name);
            var closing=new System.ComponentModel.CancelEventArgs();typeof(SetupWindow).GetMethod("Setup_Closing",All).Invoke(setup,new object[]{null,closing});Assert(closing.Cancel,"Busy installation window allowed destruction");
            typeof(SetupWindow).GetMethod("SetWorking",All).Invoke(setup,new object[]{false});
        });
        Check("Selected game remains empty and no running game is claimed without explicit directory",()=>{
            Assert((int)typeof(MainWindow).GetMethod("ResolveSelectedGameProcessId",All).Invoke(window,null)==0,"Unselected game was targeted");
        });
        Check("Portable diagnostics copies a bounded tail and leaves game logs unchanged",()=>{
            string game=Path.Combine(AppContext.BaseDirectory,"diagnostic-fixture");Directory.CreateDirectory(Path.Combine(game,"PEAK_Data","Managed"));Directory.CreateDirectory(Path.Combine(game,"BepInEx"));
            File.WriteAllText(Path.Combine(game,"PEAK.exe"),"fixture only");File.WriteAllText(Path.Combine(game,"PEAK_Data","Managed","Assembly-CSharp.dll"),"fixture assembly");
            string log=Path.Combine(game,"BepInEx","LogOutput.log");File.WriteAllText(log,new string('x',3*1024*1024)+"LOG-TAIL-END");long original=new FileInfo(log).Length;
            var diagnostics=typeof(MainWindow).Assembly.GetType("PeakWorkshop.Desktop.DiagnosticsService");string target=(string)diagnostics.GetMethod("Collect",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{game});
            string copy=Path.Combine(target,"BepInEx.LogOutput.log");Assert(new FileInfo(copy).Length<=1024*1024+4&&File.ReadAllText(copy).EndsWith("LOG-TAIL-END"),"Diagnostics unbounded or missed tail");Assert(new FileInfo(log).Length==original,"Source log altered");Assert(File.Exists(Path.Combine(target,"diagnostics.json")),"Diagnostic metadata missing");
        });
        Check("Fresh production window keeps all 51 controls disabled and off",()=>{
            Assert(Cards(window).Length==51 && Cards(window).All(c=>InputsEnabled(c,false)),"Fresh inputs enabled/missing");
            Assert(Cards(window).All(c=>((CheckBox)Field(c,"Toggle")).IsChecked!=true),"Feature enabled at startup");
        });
        Check("One-click clear is a separate prominent fixed action and starts disabled",()=>{
            Layout(window);
            var button=ClearButton(window);var viewport=Viewport(window);
            Assert(button.Content.ToString()=="一键清除当前状态" && !button.IsEnabled,"Clear button missing/enabled initially");
            Assert(!viewport.IsAncestorOf(button),"Clear button scrolls with feature list");
            var root=(UIElement)window.Content;
            double buttonTop=button.TranslatePoint(new Point(),root).Y;
            double viewportTop=viewport.TranslatePoint(new Point(),root).Y;
            Assert(button.ActualHeight>=35 && buttonTop+button.ActualHeight<=viewportTop,"Clear action not prominent above list");
            Assert(!((DispatcherTimer)Field(window,"_pollTimer")).IsEnabled && !window.IsLoaded,"Offscreen layout started game IPC lifecycle");
        });
        Check("Ready airport state enables the actual production toggles and multiplier inputs",()=>{
            Invoke(window,"ApplyActual",State(true));
            Assert(Cards(window).All(c=>InputsEnabled(c,true)),"Ready controls stayed disabled");
            Assert(window.CurrentStatus=="已连接 · 角色就绪","Ready title mismatch");
            Assert(ClearButton(window).IsEnabled,"Ready clear action disabled");
        });
        Check("Nonready state disables inputs and displays the plugin's concrete reason",()=>{
            Invoke(window,"ApplyActual",State(false));
            Assert(Cards(window).All(c=>InputsEnabled(c,false)),"Nonready inputs enabled");
            Assert(((TextBlock)window.FindName("ConnectionDetail")).Text=="角色组件尚未完成初始化。","Header reason lost");
            Assert(Cards(window).All(c=>((TextBlock)Field(c,"Status")).Text.Contains("角色组件尚未完成初始化")),"Card reason lost");
            Assert(!ClearButton(window).IsEnabled && ((TextBlock)window.FindName("ClearStatusDetail")).Text.Contains("角色组件尚未完成初始化"),"Nonready clear gate/reason missing");
        });
        Check("Older protocol state without a diagnostic keeps a useful waiting message",()=>{
            var state=State(false);state.characterReadinessMessage="";Invoke(window,"ApplyActual",state);
            Assert(((TextBlock)window.FindName("ConnectionDetail")).Text=="等待本地角色就绪","Fallback missing");
        });
        Check("Unavailable capability stays disabled while ready capabilities remain interactive",()=>{
            var state=State(true);state.features[0].available=false;state.features[0].reason="fixture unavailable";
            Invoke(window,"ApplyActual",state);
            Assert(Cards(window).Count(c=>InputsEnabled(c,false))==1,"Capability gate missing");
            Invoke(window,"SetBusy",true);Assert(Cards(window).All(c=>InputsEnabled(c,false)),"Busy gate missing");
            Invoke(window,"SetBusy",false);Assert(Cards(window).Count(c=>InputsEnabled(c,true))==50,"Busy recovery failed");
        });
        Check("Clear capability denial and legacy fallback explain why the action is disabled",()=>{
            var state=State(true);state.clearStatusAvailable=false;state.clearStatusReason="fixture clear unavailable";
            Invoke(window,"ApplyActual",state);
            Assert(!ClearButton(window).IsEnabled && ((TextBlock)window.FindName("ClearStatusDetail")).Text=="fixture clear unavailable","Clear capability reason missing");
            state.clearStatusReason="";Invoke(window,"ApplyActual",state);
            Assert(!ClearButton(window).IsEnabled && ((TextBlock)window.FindName("ClearStatusDetail")).Text.Contains("不支持"),"Old plugin clear fallback missing");
        });
        Check("Clear action respects busy, closing and stopped gates without changing toggles",()=>{
            var state=State(true);state.features[0].enabled=true;Invoke(window,"ApplyActual",state);
            var before=Cards(window).Select(c=>((CheckBox)Field(c,"Toggle")).IsChecked).ToArray();
            Invoke(window,"SetBusy",true);Assert(!ClearButton(window).IsEnabled,"Busy clear action enabled");
            Invoke(window,"ClearStatus_Click",ClearButton(window),new RoutedEventArgs());
            Invoke(window,"SetBusy",false);Assert(ClearButton(window).IsEnabled,"Clear busy recovery failed");
            SetField(window,"_closing",true);Invoke(window,"UpdateEnabled");Assert(!ClearButton(window).IsEnabled,"Closing clear action enabled");
            Invoke(window,"ClearStatus_Click",ClearButton(window),new RoutedEventArgs());
            SetField(window,"_closing",false);SetField(window,"_stoppedPid",123);Invoke(window,"UpdateEnabled");
            Assert(!ClearButton(window).IsEnabled,"Stopped PID clear action enabled");
            Invoke(window,"ClearStatus_Click",ClearButton(window),new RoutedEventArgs());
            SetField(window,"_stoppedPid",0);state.pluginStopped=true;Invoke(window,"ApplyActual",state);
            Assert(!ClearButton(window).IsEnabled,"Stopped protocol state clear action enabled");
            Invoke(window,"ClearStatus_Click",ClearButton(window),new RoutedEventArgs());
            Assert(Cards(window).Select(c=>((CheckBox)Field(c,"Toggle")).IsChecked).SequenceEqual(before),"Clear gating changed feature toggles");
            Invoke(window,"ApplyActual",State(true));
        });
        Check("Every sidebar entry locates its header inside the same complete feature list",()=>{
            Layout(window);
            foreach(var group in Groups(window))
            {
                string name=(string)Field(group,"Name");ClickNavigation(window,name);AssertAllVisible(window);
                double target=Math.Min(GroupTop(window,group),Viewport(window).ScrollableHeight);
                Assert(Math.Abs(Viewport(window).VerticalOffset-target)<1.5,"Anchor offset mismatch for "+name);
                Assert(SelectedGroup(window)==name,"Selected sidebar mismatch for "+name+": "+SelectedGroup(window));
            }
        });
        Check("Ordinary list scrolling updates the highlighted matching sidebar entry",()=>{
            var group=Groups(window)[1];string name=(string)Field(group,"Name");
            Scroll(window,GroupTop(window,group)+20);
            Assert(SelectedGroup(window)==name,"Scroll event did not select visible group");
            Assert(((SolidColorBrush)Navigation(window,name).Background).Color==(Color)ColorConverter.ConvertFromString("#27352C"),"Active sidebar brush missing");
        });
        Check("Bottom-of-list scrolling highlights the final fog section",()=>{
            Viewport(window).ScrollToBottom();Layout(window);
            Assert(SelectedGroup(window)=="迷雾","Bottom did not select last group");
        });
        Check("All features menu returns to the top without filtering the list",()=>{
            ClickNavigation(window,"");AssertAllVisible(window);
            Assert(Viewport(window).VerticalOffset<1,"All-features menu did not scroll to top");
        });
        Check("Search and availability filters still work and menu anchors reveal their targets",()=>{
            ((TextBox)window.FindName("SearchBox")).Text="fog.stop";Layout(window);
            Assert(Cards(window).Count(c=>((FrameworkElement)Field(c,"Container")).Visibility==Visibility.Visible)==1,"Search did not filter actual cards");
            ClickNavigation(window,"体力");AssertAllVisible(window);
            Assert(((TextBox)window.FindName("SearchBox")).Text=="" && SelectedGroup(window)=="体力","Menu did not clear search/locate target");
            var state=State(true);state.features[0].available=false;Invoke(window,"ApplyActual",state);
            ((CheckBox)window.FindName("AvailableOnlyBox")).IsChecked=true;Layout(window);
            Assert(Cards(window).Count(c=>((FrameworkElement)Field(c,"Container")).Visibility==Visibility.Visible)==50,"Availability filter ignored capability");
            ClickNavigation(window,"移动与攀爬");AssertAllVisible(window);
            Assert(((CheckBox)window.FindName("AvailableOnlyBox")).IsChecked==false,"Menu did not clear conflicting availability filter");
            Invoke(window,"ApplyActual",State(true));Layout(window);
        });
        Check("Polling state refresh preserves current scroll position and selected group",()=>{
            ClickNavigation(window,"单项状态");Scroll(window,Viewport(window).VerticalOffset+90);
            double offset=Viewport(window).VerticalOffset;string selected=SelectedGroup(window);
            for(int i=0;i<3;i++){Invoke(window,"ApplyActual",State(true));Layout(window);}
            Assert(Math.Abs(Viewport(window).VerticalOffset-offset)<1,"State polling jumped scroll position");
            Assert(SelectedGroup(window)==selected,"State polling changed selected group");
        });
        Check("Resizing preserves the list and sidebar follows recalculated group positions",()=>{
            Layout(window,1020,730);
            Assert(Viewport(window).ViewportHeight>80,"Small supported window lost feature viewport");
            var group=Groups(window)[1];double top=GroupTop(window,group);
            Viewport(window).ScrollToVerticalOffset(top+10);Layout(window,1020,730);
            Assert(SelectedGroup(window)=="体力","Resize/scroll did not update navigation");AssertAllVisible(window);
            double offset=Viewport(window).VerticalOffset;Layout(window,1020,730);
            Assert(Math.Abs(Viewport(window).VerticalOffset-offset)<1,"Unchanged resize layout jumped scroll");
            Layout(window);
        });
        Check("Airport-map-airport preserves selected switches and multipliers while loading suspends inputs",()=>{
            var ready=State(true);ready.features[0].enabled=true;ready.features[0].value=17.4f;
            Invoke(window,"ApplyActual",ready);
            foreach(string scene in new[]{"Loading","Level_0","Loading","Airport"})
            {
                var state=State(scene!="Loading");state.scene=scene;state.features[0].enabled=true;state.features[0].value=17.4f;
                Invoke(window,"ApplyActual",state);
                object card=Cards(window)[0];Assert(((CheckBox)Field(card,"Toggle")).IsChecked==true,"Scene transition unchecked selection");
                Assert((float)Field(card,"ActualValue")==17.4f,"Scene transition reset multiplier");
                Assert(InputsEnabled(card,scene!="Loading"),"Readiness failed to gate temporary effects");
                if(scene=="Loading")Assert(((TextBlock)Field(card,"Status")).Text.Contains("已保留"),"Paused selection not explained");
            }
        });
        Check("Disconnect disables inputs and preserves selected switches until same-window recovery",()=>{
            var state=State(true);state.features[0].enabled=true;state.features[0].value=17.4f;Invoke(window,"ApplyActual",state);
            Invoke(window,"SetDisconnected","fixture disconnected");
            Assert(Cards(window).All(c=>InputsEnabled(c,false)),"Disconnected inputs enabled");
            Assert(((CheckBox)Field(Cards(window)[0],"Toggle")).IsChecked==true,"Disconnect unchecked selection");
            Assert(((TextBlock)Field(Cards(window)[0],"Status")).Text.Contains("已保留"),"Disconnected selection looks active");
            Assert(!ClearButton(window).IsEnabled,"Disconnected clear action enabled");
            Invoke(window,"ApplyActual",state);Assert(InputsEnabled(Cards(window)[0],true),"Recovery did not restore controls");
        });
        Check("Confirmed manual reset clears retained choices and a new game session uses its reported defaults",()=>{
            var reset=State(true);Invoke(window,"ApplyActual",reset);
            Assert(Cards(window).All(c=>((CheckBox)Field(c,"Toggle")).IsChecked!=true),"Confirmed reset retained enabled controls");
            reset.sessionId="new-game-session";Invoke(window,"ApplyActual",reset);
            Assert(Cards(window).All(c=>((CheckBox)Field(c,"Toggle")).IsChecked!=true),"Old choices replayed into a new game session");
        });
        Check("Offscreen regression never starts the poll timer or game connection",()=>{
            Assert(!window.IsLoaded && !((DispatcherTimer)Field(window,"_pollTimer")).IsEnabled,"Test started production lifecycle");
            var client=Field(window,"_client");
            Assert((int)client.GetType().GetProperty("ProcessId",All).GetValue(client)==0,"Test connected to real game process");
        });

        var infoType=typeof(MainWindow).Assembly.GetType("PeakWorkshop.Desktop.GameVersionInfo");
        var info=infoType.GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{args.Length > 1 ? args[1] : "",System.Threading.CancellationToken.None});
        Invoke(window,"SetTrainerVersionInfo");
        Invoke(window,"ApplyGameVersionInfo",info);
        var gameLabel=(TextBlock)window.FindName("GameBuildText");
        var trainerLabel=(TextBlock)window.FindName("TrainerVersionText");
        var gameTip=(ToolTip)gameLabel.ToolTip;
        var trainerTip=(ToolTip)trainerLabel.ToolTip;
        Check("Version tooltips contain the full hashes with visible text and wrapping",()=>{
            foreach(var label in new[]{gameLabel,trainerLabel}){
                var tip=(ToolTip)label.ToolTip;var text=(TextBlock)tip.Content;
                Assert(text.Text.Contains((string)label.Tag),"Full hash missing from actual tooltip content");
                Assert(((SolidColorBrush)text.Foreground).Color!=((SolidColorBrush)text.Background).Color,"Tooltip text matches background");
                Assert(text.TextWrapping==TextWrapping.Wrap,"Long hash text can be cut off");
            }
        });
        Check("Metadata polling updates tooltip content without replacing the hovered tooltip",()=>{
            Invoke(window,"ApplyGameVersionInfo",info);
            Assert(ReferenceEquals(gameTip,gameLabel.ToolTip),"Polling replaces tooltip instance");
        });
        string imageRoot=Path.GetDirectoryName(Path.GetFullPath(args[0]));
        void RenderTooltip(ToolTip tip,string file){
            tip.ApplyTemplate();tip.Measure(new Size(640,700));tip.Arrange(new Rect(tip.DesiredSize));tip.UpdateLayout();
            var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(tip.ActualWidth),(int)Math.Ceiling(tip.ActualHeight),96,96,PixelFormats.Pbgra32);
            image.Render(tip);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using(var stream=File.Create(Path.Combine(imageRoot,file)))encoder.Save(stream);

        }
        RenderTooltip(new ToolTip{Content=((TextBlock)gameTip.Content).Text},"tooltip-before.png");
        RenderTooltip(gameTip,"tooltip-game-fixed.png");
        RenderTooltip(trainerTip,"tooltip-trainer-fixed.png");
        File.WriteAllText(Path.ChangeExtension(args[0],".metadata.json"),JsonSerializer.Serialize(new{
            game=gameLabel.Text,trainer=trainerLabel.Text,
            gameTooltip=((TextBlock)gameTip.Content).Text,
            trainerTooltip=((TextBlock)trainerTip.Content).Text
        },new JsonSerializerOptions{WriteIndented=true}));

        ((IDisposable)Field(window,"_client")).Dispose();
        File.WriteAllText(args[0],JsonSerializer.Serialize(new{total=results.Count,passed=results.Count-failed,failed,
            method="Actual compiled WPF App/MainWindow with synthetic states; window never shown, no game IPC",createdUtc=DateTime.UtcNow,results},new JsonSerializerOptions{WriteIndented=true}));
        app.Shutdown();return failed==0?0:1;
    }
}
