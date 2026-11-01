using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodexPushover;

public static class UiRenderHarness
{
    sealed class Node
    {
        public Control Control;
        public string Path;
        public Rectangle Bounds;
        public Rectangle VisibleBounds;
        public Rectangle LayoutVisibleBounds;
        public bool ScrollChild;
    }
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    static readonly List<object> Results = new List<object>();
    [STAThread]
    public static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : Path.Combine(Path.GetDirectoryName(typeof(UiRenderHarness).Assembly.Location), "ui-validation");
        Directory.CreateDirectory(output);
        string scenario = args.Length > 1 ? args[1] : "preview";
        SetThreadDpiAwarenessContext(new IntPtr(scenario == "native-dpi" ? -2 : -1));
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        switch(scenario)
        {
            case "preview": Run(output,scenario,new Size(920,860),false,1f);break;
            case "minimum": Run(output,scenario,new Size(760,620),true,1f);break;
            case "minimum-scrolled": Run(output,scenario,new Size(760,620),true,1f);break;
            case "wide": Run(output,scenario,new Size(1200,860),false,1f);break;
            case "scale-125": Run(output,scenario,new Size(920,860),false,1.25f);break;
            case "scale-150": Run(output,scenario,new Size(920,860),false,1.5f);break;
            case "scale-200": Run(output,scenario,new Size(920,860),false,2f);break;
            case "native-dpi": Run(output,scenario,new Size(920,860),false,1f);break;
            default: throw new ArgumentException("Unknown preview scenario.");
        }
        string json = new JavaScriptSerializer { MaxJsonLength = 1000000 }.Serialize(new { method="Own WinForms control layout and DrawToBitmap; no tray, sender, API, production options or credential access", scenarios=Results });
        File.WriteAllText(Path.Combine(output,scenario+".json"),json,new UTF8Encoding(false));
        Console.WriteLine(json); return 0;
    }
    static void Run(string output, string name, Size size, bool outer, float scale)
    {
        using(var form = new SettingsForm(null))
        {
            Size constructorClient=form.ClientSize;
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000,-30000);
            if(name!="native-dpi") { if(outer) form.Size=size; else form.ClientSize=size; }
            IntPtr unused=form.Handle;
            var before=form.AutoScaleDimensions; form.PerformAutoScale(); var after=form.AutoScaleDimensions;
            if(scale!=1f)
            {
                form.Scale(new SizeF(scale,scale));
                form.ClientSize=new Size((int)Math.Round(size.Width*scale),(int)Math.Round(size.Height*scale));
            }
            for(int i=0;i<3;i++) { Layout(form); Application.DoEvents(); }
            if (name == "minimum-scrolled")
            {
                var input = All(form).OfType<NumericUpDown>().First();
                foreach (var panel in All(form).OfType<Panel>()) if (panel.AutoScroll) panel.ScrollControlIntoView(input);
                Application.DoEvents();
            }
            var nodes = new List<Node>(); Collect(form,form,"Form",false,nodes);
            var errors = new List<string>(); var ellipses = new List<string>(); var details = new List<object>();
            var client = form.ClientRectangle;
            foreach(var node in nodes)
            {
                Control c=node.Control;
                if(c.Width<=0||c.Height<=0) { errors.Add(node.Path+" has non-positive bounds"); continue; }
                if(!node.ScrollChild && !client.Contains(node.Bounds)) errors.Add(node.Path+" is outside form client: "+node.Bounds);
                var label=c as Label;
                if(label!=null && label.Text.Length>0)
                {
                    TextFormatFlags flags=TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl;
                    Size measured=TextRenderer.MeasureText(label.Text,label.Font,new Size(Int32.MaxValue,Int32.MaxValue),flags);
                    bool canEllipsis=label.AutoEllipsis;
                    if(measured.Width>label.ClientSize.Width && canEllipsis) ellipses.Add(node.Path+" intentionally ellipsizes: "+label.Text);
                    else if(measured.Width>label.ClientSize.Width+2) errors.Add(node.Path+" text wider than box ("+measured.Width+">"+label.Width+"): "+label.Text);
                    if(measured.Height>label.ClientSize.Height+2) errors.Add(node.Path+" text taller than box ("+measured.Height+">"+label.Height+"): "+label.Text);
                }
                var blue=c as BlueButton;
                if(blue!=null)
                {
                    Size measured=TextRenderer.MeasureText(blue.Text,blue.Font);
                    if(measured.Width+12>blue.Width || measured.Height+4>blue.Height) errors.Add(node.Path+" primary action text cramped");
                }
                if(c is NumericUpDown && (c.Width<50 || node.LayoutVisibleBounds.Width<50 || node.LayoutVisibleBounds.Height<c.Height-2)) errors.Add(node.Path+" numeric input collapsed/clipped: bounds="+node.Bounds+" visible="+node.LayoutVisibleBounds);
                if(name == "minimum-scrolled" && c is NumericUpDown && node.VisibleBounds.Height<c.Height-2) errors.Add("Numeric input cannot be reached by scrolling.");
                if(c is BlueSwitch)
                {
                    float dpi; using(var g=c.CreateGraphics()) dpi=g.DpiX;
                    int remaining=Math.Max(0,c.Width-(int)Math.Ceiling(52*dpi/96f));
                    var measured=TextRenderer.MeasureText(c.Text,c.Font);
                    if(measured.Width>remaining) ellipses.Add(node.Path+" switch text ellipsizes");
                    if(remaining<=20 || c.Height<22*dpi/96f) errors.Add(node.Path+" switch track/text too small");
                }
                var ancestors=new List<object>(); Control parent=c.Parent; while(parent!=null && parent!=form) { var table=parent as TableLayoutPanel; var columns=new List<string>(); var rows=new List<string>(); if(table!=null) { foreach(ColumnStyle col in table.ColumnStyles)columns.Add(col.SizeType+":"+col.Width);foreach(RowStyle row in table.RowStyles)rows.Add(row.SizeType+":"+row.Height); } ancestors.Add(new{type=parent.GetType().Name,bounds=parent.Bounds.ToString(),display=parent.DisplayRectangle.ToString(),columnStyles=columns,rowStyles=rows});parent=parent.Parent; }
                details.Add(new { ancestors=ancestors,path=node.Path,type=c.GetType().Name,localBounds=c.Bounds.ToString(),dock=c.Dock.ToString(),margin=c.Margin.ToString(),tableCell=(c.Parent is TableLayoutPanel?((TableLayoutPanel)c.Parent).GetColumn(c).ToString()+","+((TableLayoutPanel)c.Parent).GetRow(c).ToString():""),columnWidths=(c.Parent is TableLayoutPanel?String.Join(",",((TableLayoutPanel)c.Parent).GetColumnWidths()):""),rowHeights=(c.Parent is TableLayoutPanel?String.Join(",",((TableLayoutPanel)c.Parent).GetRowHeights()):""),text=(c is TextBox?"[empty masked credential field]":c.Text),bounds=new[]{node.Bounds.X,node.Bounds.Y,node.Bounds.Width,node.Bounds.Height},scrollChild=node.ScrollChild,font=c.Font.Name,fontPoints=c.Font.SizeInPoints });
            }
            for(int i=0;i<nodes.Count;i++) for(int j=i+1;j<nodes.Count;j++)
            {
                Control a=nodes[i].Control,b=nodes[j].Control;
                if(Important(a)&&Important(b) && nodes[i].VisibleBounds.IntersectsWith(nodes[j].VisibleBounds)) errors.Add("Important controls overlap: "+nodes[i].Path+" / "+nodes[j].Path);
            }
            using(var bitmap=new Bitmap(form.Width,form.Height,PixelFormat.Format32bppArgb))
            {
                form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));
                bitmap.Save(Path.Combine(output,name+".png"),ImageFormat.Png);
            }
            int scrollHeight=0,scrollViewport=0;
            foreach(Control c in All(form)) if(c is Panel && ((Panel)c).AutoScroll) { scrollHeight=((Panel)c).DisplayRectangle.Height;scrollViewport=c.ClientSize.Height; }
            if (name == "preview" && scrollHeight > scrollViewport) errors.Add("Default window requires scrolling to see its settings.");
            Results.Add(new { scenario=name,constructorClient=new[]{constructorClient.Width,constructorClient.Height},clientSize=new[]{form.ClientSize.Width,form.ClientSize.Height},outerSize=new[]{form.Width,form.Height},scale=scale,autoScaleBefore=before.ToString(),autoScaleAfter=after.ToString(),currentAutoScale=form.CurrentAutoScaleDimensions.ToString(),errors=errors,expectedEllipsis=ellipses,scrollHeight=scrollHeight,scrollViewport=scrollViewport,leafCount=nodes.Count,details=details });
        }
    }
    static bool Important(Control c) { return c is BlueButton || c is BlueSwitch || c is TextBox || c is NumericUpDown; }
    static IEnumerable<Control> All(Control parent)
    { foreach(Control c in parent.Controls) { yield return c; foreach(Control child in All(c))yield return child; } }
    static void Layout(Control c)
    {
        c.CreateControl(); IntPtr unused=c.Handle;
        c.PerformLayout(); foreach(Control child in c.Controls)Layout(child); c.PerformLayout();
    }
    static void Collect(Control c,Form form,string path,bool scroll,List<Node> nodes)
    {
        if(c is Panel && ((Panel)c).AutoScroll) scroll=true;
        bool leaf=c is Label || c is TextBox || c is Button || c is CheckBox || c is NumericUpDown;
        if(leaf)
        {
            Point p=new Point(); Control part=c; while(part!=form && part!=null){p.Offset(part.Left,part.Top);part=part.Parent;}
            Rectangle visible=new Rectangle(p,c.Size),layoutVisible=visible; Control ancestor=c.Parent; bool passedScroll=false;
            while(ancestor!=null && ancestor!=form)
            {
                Point q=new Point(); Control item=ancestor; while(item!=form && item!=null){q.Offset(item.Left,item.Top);item=item.Parent;}
                Rectangle parentBounds=new Rectangle(q,ancestor.ClientSize);
                visible=Rectangle.Intersect(visible,parentBounds);
                if(ancestor is Panel && ((Panel)ancestor).AutoScroll) passedScroll=true;
                if(!passedScroll) layoutVisible=Rectangle.Intersect(layoutVisible,parentBounds);
                ancestor=ancestor.Parent;
            }
            nodes.Add(new Node { Control=c,Path=path,Bounds=new Rectangle(p,c.Size),VisibleBounds=visible,LayoutVisibleBounds=layoutVisible,ScrollChild=scroll });
            return;
        }
        for(int i=0;i<c.Controls.Count;i++) { Control child=c.Controls[i];Collect(child,form,path+"/"+child.GetType().Name+"["+i+"]",scroll,nodes); }
    }
}
