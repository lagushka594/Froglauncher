# Выбор элемента окна через UI Automation (для автопроверки UI): RadioButton/Button по подсказке или имени.
# Использование: .\Ui-Select.ps1 -ProcessName FrogLauncher -Text "Подписка"
param(
    [Parameter(Mandatory)] [string] $ProcessName,
    [Parameter(Mandatory)] [string] $Text,
    # Если задано — вписать значение в поле ввода (TextBox/PasswordBox) с этой подсказкой/именем.
    [string] $Value
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process -Name $ProcessName | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)

foreach ($el in $all) {
    $c = $el.Current
    if ($c.HelpText -ne $Text -and $c.Name -ne $Text) { continue }
    $pattern = $null
    if ($PSBoundParameters.ContainsKey('Value') -and $el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.SetValue($Value); "set: $Text"; return }
    if ($PSBoundParameters.ContainsKey('Value')) { continue }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); "selected: $Text"; return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); "invoked: $Text"; return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); "toggled: $Text"; return }
}
throw "Элемент '$Text' не найден"
