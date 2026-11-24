param([Parameter(Mandatory=$true)][string]$Folder,[ValidateSet('ja','en','ko')][string]$Language)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$null=[Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]
$null=[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null=[Windows.Storage.Streams.IRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
$asTask=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function AwaitResult($operation,[Type]$resultType){
    $task=$asTask.MakeGenericMethod($resultType).Invoke($null,@($operation))
    $task.GetAwaiter().GetResult()
}
$lang=[Windows.Globalization.Language]::new($Language)
$engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang)
# The Japanese and Korean recognizers also recognize Latin script.
if(!$engine -and $Language -eq 'en'){$engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()}
if(!$engine){throw "Windows OCR language unavailable ($Language)"}
foreach($file in [IO.Directory]::EnumerateFiles($Folder,'ocr-*.jpg') | Sort-Object){
    $stream=[IO.File]::OpenRead($file)
    $random=[System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($stream)
    $bitmap=$null
    try{
        $decoder=AwaitResult ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($random)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $lines=@(foreach($band in @(0.0,0.31,0.62)){
          $bounds=New-Object Windows.Graphics.Imaging.BitmapBounds
          $bounds.X=0;$bounds.Y=[uint32]($band*$decoder.PixelHeight)
          $bounds.Width=$decoder.PixelWidth;$bounds.Height=[uint32][Math]::Min($decoder.PixelHeight-$bounds.Y,[Math]::Ceiling($decoder.PixelHeight*0.38))
          $transform=[Windows.Graphics.Imaging.BitmapTransform,Windows.Graphics.Imaging,ContentType=WindowsRuntime]::new()
          $transform.Bounds=$bounds
          $bitmap=AwaitResult ($decoder.GetSoftwareBitmapAsync('Bgra8','Ignore',$transform,'IgnoreExifOrientation','DoNotColorManage')) ([Windows.Graphics.Imaging.SoftwareBitmap])
          $result=AwaitResult ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
          foreach($line in $result.Lines){
            if(!$line.Words.Count){continue}
            $rect=$line.Words[0].BoundingRect
            $center=($bounds.Y+@($rect.Y)[0]+(@($rect.Height)[0]/2.0))/$decoder.PixelHeight
            @{text=$line.Text;y=[Math]::Round($center,3)}
          }
          $bitmap.Dispose();$bitmap=$null
        })
        $index=[int]$file.Substring($file.LastIndexOf('ocr-')+4,8)-1
        [Console]::WriteLine((ConvertTo-Json -Compress -Depth 4 -InputObject @{time=$index/2.0;lines=$lines}))
    }finally{
        if($bitmap){$bitmap.Dispose()};$random.Dispose();$stream.Dispose()
    }
}
