using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Automation;

namespace HuashuWinUse;

public class UIAElementInfo
{
    public string Ref { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double Cx { get; set; }
    public double Cy { get; set; }
    public bool Enabled { get; set; }
}

public static class UIAutomationEngine
{
    private static readonly HashSet<ControlType> ActionableTypes = new()
    {
        ControlType.Button,
        ControlType.Edit,
        ControlType.Document,
        ControlType.CheckBox,
        ControlType.RadioButton,
        ControlType.ComboBox,
        ControlType.Hyperlink,
        ControlType.MenuItem,
        ControlType.TabItem,
        ControlType.Slider,
        ControlType.ListItem
    };

    public static List<UIAElementInfo> ExtractElements(IntPtr hwnd, int winOriginX, int winOriginY, int winW, int winH)
    {
        var list = new List<UIAElementInfo>();

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return list;

            var condition = new PropertyCondition(AutomationElement.IsOffscreenProperty, false);
            var elements = root.FindAll(TreeScope.Descendants, condition);

            int index = 1;
            foreach (AutomationElement el in elements)
            {
                if (list.Count >= 150) break;

                try
                {
                    var ct = el.Current.ControlType;
                    if (!ActionableTypes.Contains(ct)) continue;

                    var rect = el.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width <= 2 || rect.Height <= 2) continue;

                    double cx = rect.X + rect.Width / 2.0 - winOriginX;
                    double cy = rect.Y + rect.Height / 2.0 - winOriginY;

                    if (cx < 0 || cy < 0 || cx > winW || cy > winH) continue;

                    string name = el.Current.Name ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = el.Current.AutomationId ?? string.Empty;
                    }
                    if (name.Length > 40) name = name[..40];

                    list.Add(new UIAElementInfo
                    {
                        Ref = $"e{index++}",
                        Role = ct.ProgrammaticName.Replace("ControlType.", ""),
                        Title = name,
                        Cx = cx,
                        Cy = cy,
                        Enabled = el.Current.IsEnabled
                    });
                }
                catch { }
            }
        }
        catch { }

        return list;
    }

    public static int CountEditables(int pid, bool printDetails)
    {
        int count = 0;
        try
        {
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)
            );

            var rootCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            var appRoots = AutomationElement.RootElement.FindAll(TreeScope.Children, rootCondition);

            foreach (AutomationElement appRoot in appRoots)
            {
                var edits = appRoot.FindAll(TreeScope.Descendants, condition);
                foreach (AutomationElement ed in edits)
                {
                    count++;
                    if (printDetails && count <= 5)
                    {
                        string name = ed.Current.Name ?? ed.Current.AutomationId ?? "";
                        Console.WriteLine($"  可编辑控件: {ed.Current.ControlType.ProgrammaticName} name=\"{name}\"");
                    }
                }
            }
        }
        catch { }

        return count;
    }

    public static (bool ok, string readback) SetFirstEditableValue(int pid, string value)
    {
        try
        {
            var rootCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
            var appRoots = AutomationElement.RootElement.FindAll(TreeScope.Children, rootCondition);

            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)
            );

            foreach (AutomationElement appRoot in appRoots)
            {
                var edits = appRoot.FindAll(TreeScope.Descendants, condition);
                foreach (AutomationElement ed in edits)
                {
                    if (ed.TryGetCurrentPattern(ValuePattern.Pattern, out object? patObj) && patObj is ValuePattern vp)
                    {
                        vp.SetValue(value);
                        string readback = vp.Current.Value ?? "";
                        return (true, readback);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        return (false, "未找到支持 ValuePattern 的可编辑控件");
    }
}
