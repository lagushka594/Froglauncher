# Мужская озвучка (Microsoft Pavel, OneCore) через WinRT SpeechSynthesizer.
# Каждая реплика -> audio\vo_N.wav
param([string]$OutDir = "$PSScriptRoot\audio")

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media.SpeechSynthesis, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType = WindowsRuntime]

$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(); $task.Result }

$synth = New-Object Windows.Media.SpeechSynthesis.SpeechSynthesizer
$synth.Voice = [Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices | Where-Object { $_.DisplayName -like '*Pavel*' } | Select-Object -First 1
if (-not $synth.Voice) { throw 'Голос Pavel не найден' }

# rate/pitch подобраны под энергичную подачу; паузы — через <break>
$lines = @(
  '<prosody rate="+18%" pitch="+4%">Ты всё ещё заходишь в игру, чтобы поменять FOV?<break time="120ms"/> Серьёзно?</prosody>',
  '<prosody rate="+22%">Запустил.<break time="80ms"/> Зашёл.<break time="80ms"/> Покрутил.<break time="80ms"/> Вышел.<break time="150ms"/> И так каждый раз, когда захотел что-то поменять.</prosody>',
  '<prosody rate="+12%" pitch="+3%">А теперь — всё в лаунчере.<break time="200ms"/> Двигаешь слайдер — сразу видишь, как изменится мир.<break time="200ms"/> Без запуска игры.</prosody>',
  '<prosody rate="+12%">Не разбираешься в настройках?<break time="150ms"/> Лаунчер сам подберёт под твой компьютер.<break time="200ms"/> Один клик — и всё готово.</prosody>',
  '<prosody rate="+10%" pitch="+3%">Запустил — и играешь с идеальными настройками.<break time="150ms"/> Без танцев с бубном.</prosody>',
  '<prosody rate="+5%" pitch="+5%">Бесплатно.<break time="150ms"/> Ссылка в профиле.</prosody>'
)

for ($i = 0; $i -lt $lines.Count; $i++) {
  $ssml = '<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="ru-RU">' + $lines[$i] + '</speak>'
  $stream = Await ($synth.SynthesizeSsmlToStreamAsync($ssml)) ([Windows.Media.SpeechSynthesis.SpeechSynthesisStream])
  $reader = New-Object Windows.Storage.Streams.DataReader($stream.GetInputStreamAt(0))
  $size = [uint32]$stream.Size
  $null = Await ($reader.LoadAsync($size)) ([uint32])
  $bytes = New-Object byte[] $size
  $reader.ReadBytes($bytes)
  [IO.File]::WriteAllBytes((Join-Path $OutDir "vo_$($i + 1).wav"), $bytes)
  Write-Output "vo_$($i + 1).wav $size bytes"
}
