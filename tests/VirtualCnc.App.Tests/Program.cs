using System.Net;
using System.Net.Sockets;
using System.Reflection;
using VirtualCnc.App;
using VirtualCnc.Core;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        var folder=Path.Combine(Path.GetTempPath(),"virtual-cnc-ui-"+Guid.NewGuid());Directory.CreateDirectory(folder);
        int result=1;
        var engine=new GrblController(Path.Combine(folder,"eeprom.json"));
        using var form=new MainForm(engine);
        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(Screen.AllScreens.Max(screen=>screen.Bounds.Right)+100,100);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(MainForm).GetField("connectionSettingsPath",flags)!.SetValue(form,Path.Combine(folder,"connection.json"));
        var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        var path=(Control)typeof(MainForm).GetField("path",flags)!.GetValue(form)!;
        int invalidations=0;path.Invalidated+=(_,_)=>invalidations++;
        void Require(bool value,string message){if(!value)throw new Exception(message);}
        void Settings(bool save,Action<Form>? inspect=null)
        {
            using var timer=new System.Windows.Forms.Timer {Interval=150};
            int ticks=0;int baseline=0;
            timer.Tick+=(_,_)=>{
                var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f!=form);
                if(dialog is null)return;
                if(ticks++==0){baseline=invalidations;inspect?.Invoke(dialog);return;}
                if(ticks<5)return;
                Require(invalidations==baseline,"Preview redrew behind connection settings");
                timer.Stop();
                var layout=dialog.Controls.OfType<TableLayoutPanel>().Single();
                if(save){layout.Controls.OfType<ComboBox>().Single().SelectedItem="TCP";layout.Controls.OfType<TextBox>().Single().Text="127.0.0.1";layout.Controls.OfType<NumericUpDown>().Single().Value=port;}
                if(save&&Environment.GetEnvironmentVariable("VIRTUAL_CNC_UI_CAPTURE") is {} capture){dialog.Refresh();using var bitmap=new Bitmap(dialog.Width,dialog.Height);dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,dialog.Size));bitmap.Save(capture);}
                var buttons=layout.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>();
                buttons.Single(b=>b.Text==(save?"OK":"Отмена")).PerformClick();
            };
            timer.Start();typeof(MainForm).GetMethod("ShowConnectionSettings",flags)!.Invoke(form,null);
        }
        form.Shown+=async(_,_)=>{
            try
            {
                Settings(true);
                Require(File.Exists(Path.Combine(folder,"connection.json")),"Settings were not saved");
                typeof(MainForm).GetMethod("Connect",flags)!.Invoke(form,null);
                using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,port);
                var stream=client.GetStream();using var reader=new StreamReader(stream);
                Require((await reader.ReadLineAsync())!.StartsWith("Grbl "),"Missing TCP banner");
                await stream.WriteAsync("$X\n$20=0\n$22=0\nG91G1X10F60\n"u8.ToArray());
                for(int i=0;i<4;i++)Require(await reader.ReadLineAsync()=="ok","Command failed");
                var before=engine.Snapshot().MachinePosition.X;
                Settings(false,dialog=>{
                    var type=dialog.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ComboBox>().Single();
                    Require(!type.Enabled,"Connected settings were editable");
                });
                Require(engine.Snapshot().MachinePosition.X>before,"Opening settings stopped simulation");
                await stream.WriteAsync("?"u8.ToArray());Require((await reader.ReadLineAsync())!.StartsWith("<Run|"),"TCP stopped during settings");
                int rendered=invalidations;await Task.Delay(150);Require(invalidations>rendered,"Preview did not resume");
                using(var second=new TcpClient()){await second.ConnectAsync(IPAddress.Loopback,port);using var deadline=new CancellationTokenSource(2000);var bytes=new byte[1];Require(await second.GetStream().ReadAsync(bytes,deadline.Token)==0,"Second client was accepted");}
                Console.WriteLine("PASS: GUI TCP settings, saved endpoint, single client, GRBL motion, frozen preview with live simulation and resume");result=0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);result=1;}
            finally{form.Close();}
        };
        Application.Run(form);
        try{Directory.Delete(folder,true);}catch(IOException){}
        return result;
    }
}
