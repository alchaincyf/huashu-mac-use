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
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                var subList = new List<UIAElementInfo>();
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return subList;

                var condition = new PropertyCondition(AutomationElement.IsOffscreenProperty, false);
                var elements = root.FindAll(TreeScope.Descendants, condition);

                int index = 1;
                foreach (AutomationElement el in elements)
                {
                    if (subList.Count >= 150) break;

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

                        subList.Add(new UIAElementInfo
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
                return subList;
            });

            if (task.Wait(TimeSpan.FromSeconds(3)))
            {
                return task.Result;
            }
            else
            {
                Console.WriteLine("  [WARNING] UIA 遍历超时（>3s），目标应用可能处于忙碌状态，安全降级为空列表。");
                return list;
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

    public static (bool ok, string message) TriggerElement(IntPtr hwnd, string elRef, int winOriginX, int winOriginY, int winW, int winH)
    {
        try
        {
            if (WindowManager.IsIconic(hwnd))
            {
                SafetyGates.ShowWindow(hwnd, SafetyGates.SW_RESTORE);
                Thread.Sleep(250);
            }

            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return (false, "Cannot get AutomationElement from window handle");

            var condition = new PropertyCondition(AutomationElement.IsOffscreenProperty, false);
            var elements = root.FindAll(TreeScope.Descendants, condition);

            int index = 1;
            foreach (AutomationElement el in elements)
            {
                try
                {
                    var ct = el.Current.ControlType;
                    if (!ActionableTypes.Contains(ct)) continue;

                    var rect = el.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width <= 2 || rect.Height <= 2) continue;

                    double cx = rect.X + rect.Width / 2.0 - winOriginX;
                    double cy = rect.Y + rect.Height / 2.0 - winOriginY;
                    if (cx < 0 || cy < 0 || cx > winW || cy > winH) continue;

                    string curRef = $"e{index++}";
                    if (string.Equals(curRef, elRef, StringComparison.OrdinalIgnoreCase))
                    {
                        return ExecutePattern(el);
                    }
                }
                catch { }
            }
            return (false, $"Element {elRef} not found in active window elements");
        }
        catch (Exception ex)
        {
            return (false, $"UIA Trigger error: {ex.Message}");
        }
    }

    public static (bool ok, string message) TriggerElementByName(IntPtr hwnd, string keyword)
    {
        try
        {
            if (WindowManager.IsIconic(hwnd))
            {
                SafetyGates.ShowWindow(hwnd, SafetyGates.SW_RESTORE);
                Thread.Sleep(250);
            }

            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return (false, "Cannot get AutomationElement from window handle");

            var condition = new PropertyCondition(AutomationElement.IsOffscreenProperty, false);
            var elements = root.FindAll(TreeScope.Descendants, condition);

            foreach (AutomationElement el in elements)
            {
                try
                {
                    string name = el.Current.Name ?? "";
                    string autoId = el.Current.AutomationId ?? "";
                    if (!name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                        !autoId.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var res = ExecutePattern(el);
                    if (res.ok) return res;
                }
                catch { }
            }
            return (false, $"No actionable element matching \"{keyword}\" found");
        }
        catch (Exception ex)
        {
            return (false, $"UIA TriggerByName error: {ex.Message}");
        }
    }

    private static (bool ok, string message) ExecutePattern(AutomationElement el)
    {
        string name = el.Current.Name ?? el.Current.AutomationId ?? "";
        string role = el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");

        // 1. TogglePattern (for checkboxes, toggleswitches)
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object? tpObj) && tpObj is TogglePattern tp)
        {
            var before = tp.Current.ToggleState;
            tp.Toggle();
            Thread.Sleep(100);
            return (true, $"L1 TogglePattern: \"{name}\" ({role}) toggled from {before} to {tp.Current.ToggleState}");
        }

        // 2. InvokePattern (for buttons, menu items)
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object? ipObj) && ipObj is InvokePattern ip)
        {
            ip.Invoke();
            Thread.Sleep(100);
            return (true, $"L1 InvokePattern: \"{name}\" ({role}) invoked");
        }

        // 3. SelectionItemPattern (for list items, radio buttons, tabs)
        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? spObj) && spObj is SelectionItemPattern sp)
        {
            sp.Select();
            Thread.Sleep(100);
            return (true, $"L1 SelectionItemPattern: \"{name}\" ({role}) selected");
        }

        // 4. ExpandCollapsePattern
        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? epObj) && epObj is ExpandCollapsePattern ep)
        {
            if (ep.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                ep.Collapse();
            else
                ep.Expand();
            Thread.Sleep(100);
            return (true, $"L1 ExpandCollapsePattern: \"{name}\" ({role}) toggled");
        }

        // 5. Try children/descendants for actionable patterns (e.g. inner ToggleSwitch or Button)
        try
        {
            var children = el.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
            foreach (AutomationElement child in children)
            {
                if (child.TryGetCurrentPattern(TogglePattern.Pattern, out object? cTp) && cTp is TogglePattern cToggle)
                {
                    var before = cToggle.Current.ToggleState;
                    cToggle.Toggle();
                    Thread.Sleep(100);
                    return (true, $"L1 Child TogglePattern: \"{child.Current.Name}\" toggled from {before} to {cToggle.Current.ToggleState}");
                }
                if (child.TryGetCurrentPattern(InvokePattern.Pattern, out object? cIp) && cIp is InvokePattern cInvoke)
                {
                    cInvoke.Invoke();
                    Thread.Sleep(100);
                    return (true, $"L1 Child InvokePattern: \"{child.Current.Name}\" invoked");
                }
            }
        }
        catch { }

        return (false, $"Element \"{name}\" ({role}) does not support Toggle, Invoke, SelectionItem, or ExpandCollapse pattern");
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

