# Generates the app icon: a Segoe-style outlined window with an accent camera tucked into its
# bottom-right corner. Outputs Assets/AppIcon.svg (vector master), Assets/AppIcon.ico (16..256,
# PNG-compressed frames) and the PNG logo set the manifest points at. Sizes up to 24px use a
# bolder simplified variant so it still reads in the tray. Run from the repo root:
#   pwsh Tools/Generate-AppIcon.ps1

Add-Type -AssemblyName System.Drawing
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

$assets = Join-Path $PSScriptRoot "..\Assets"
$ink = [System.Drawing.Color]::FromArgb(255, 70, 76, 86)          # window outline
$accent = [System.Drawing.Color]::FromArgb(255, 0, 103, 192)       # camera
$knockout = [System.Drawing.Color]::Transparent                    # gap between camera and window

# Geometry in a 0..1 box. Small = bolder, fewer details (16/20/24 px).
function Spec($small) {
    if ($small) {
        @{ winX = 0.06; winY = 0.14; winW = 0.70; winH = 0.58; winR = 0.09; stroke = 0.10; bar = 0.16;
           camX = 0.44; camY = 0.46; camW = 0.52; camH = 0.44; gap = 0.09 }
    } else {
        @{ winX = 0.08; winY = 0.16; winW = 0.66; winH = 0.54; winR = 0.08; stroke = 0.06; bar = 0.13;
           camX = 0.46; camY = 0.48; camW = 0.48; camH = 0.40; gap = 0.06 }
    }
}

function RoundRect($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(0.01, $r * 2)
    $p.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
    $p.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
    $p.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
    $p.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
    $p.CloseFigure(); return $p
}

# Camera silhouette (body + flash bump) with a lens ring, in a unit-scaled box.
function CameraPath($x, $y, $w, $h) {
    $bodyY = $y + $h * 0.24
    $p = RoundRect ($x + $w * 0.30) $y ($w * 0.40) ($h * 0.34) ($w * 0.07)
    $p.AddPath((RoundRect $x $bodyY $w ($h - $h * 0.24) ($w * 0.13)), $false)
    return $p
}

function Draw($g, $size, $small) {
    $s = Spec $small
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Window: outline + solid title bar (clipped to the rounded shape).
    $win = RoundRect ($s.winX * $size) ($s.winY * $size) ($s.winW * $size) ($s.winH * $size) ($s.winR * $size)
    $pen = New-Object System.Drawing.Pen($ink, [single]($s.stroke * $size)); $pen.Alignment = 'Inset'
    $g.DrawPath($pen, $win)
    $g.SetClip($win)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $ink), [single]($s.winX * $size), [single]($s.winY * $size), [single]($s.winW * $size), [single]($s.bar * $size))
    $g.ResetClip()

    # Camera: knockout gap first, then the accent body, then the lens.
    $cx = $s.camX * $size; $cy = $s.camY * $size; $cw = $s.camW * $size; $ch = $s.camH * $size
    $cam = CameraPath $cx $cy $cw $ch
    $gapPen = New-Object System.Drawing.Pen($knockout, [single]($s.gap * $size * 2)); $gapPen.LineJoin = 'Round'
    $g.CompositingMode = 'SourceCopy'; $g.DrawPath($gapPen, $cam); $g.CompositingMode = 'SourceOver'
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), $cam)
    $lensCx = $cx + $cw / 2; $lensCy = $cy + $ch * 0.24 + ($ch - $ch * 0.24) / 2 + $ch * 0.02
    $lr = $cw * 0.21
    $g.CompositingMode = 'SourceCopy'
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $knockout), [single]($lensCx - $lr), [single]($lensCy - $lr), [single]($lr * 2), [single]($lr * 2))
    $g.CompositingMode = 'SourceOver'
    $inner = $lr * $(if ($small) { 0.55 } else { 0.62 })
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $accent), [single]($lensCx - $inner), [single]($lensCy - $inner), [single]($inner * 2), [single]($inner * 2))
}

function Render($size, $small) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); Draw $g $size $small; $g.Dispose()
    return $bmp
}

function PngBytes($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return ,$ms.ToArray() }

# --- ICO (PNG-compressed frames) ---
$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$frames = foreach ($sz in $sizes) { $bmp = Render $sz ($sz -le 24); [byte[]]$bytes = PngBytes $bmp; $bmp.Dispose(); @{ Size = $sz; Bytes = $bytes } }
$ico = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$f.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f.Bytes) }
$w.Flush(); [System.IO.File]::WriteAllBytes((Join-Path $assets "AppIcon.ico"), $ico.ToArray()); $w.Dispose()
Write-Host "wrote AppIcon.ico ($($sizes -join ', '))"

# --- PNG logo set (names the manifest / template already reference) ---
function SavePng($name, $size, $small) { $bmp = Render $size $small; $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); Write-Host "wrote $name" }
function SaveOnCanvas($name, $cw, $ch, $iconSize) {
    $canvas = New-Object System.Drawing.Bitmap $cw, $ch, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas); $g.Clear([System.Drawing.Color]::Transparent)
    $icon = Render $iconSize $false; $g.DrawImage($icon, [int](($cw - $iconSize) / 2), [int](($ch - $iconSize) / 2), $iconSize, $iconSize)
    $icon.Dispose(); $g.Dispose(); $canvas.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png); $canvas.Dispose(); Write-Host "wrote $name"
}
SavePng "Square44x44Logo.scale-200.png" 88 $false
SavePng "Square44x44Logo.targetsize-24_altform-unplated.png" 24 $true
SavePng "Square44x44Logo.targetsize-48_altform-lightunplated.png" 48 $false
SavePng "Square150x150Logo.scale-200.png" 300 $false
SavePng "StoreLogo.png" 50 $false
SavePng "LockScreenLogo.scale-200.png" 48 $false
SaveOnCanvas "Wide310x150Logo.scale-200.png" 620 300 220
SaveOnCanvas "SplashScreen.scale-200.png" 1240 600 320

# --- SVG master (same geometry, unit box scaled to 256) ---
$s = Spec $false; $u = 256
function F($v) { return ("{0:0.###}" -f $v) }
$camX = $s.camX * $u; $camY = $s.camY * $u; $camW = $s.camW * $u; $camH = $s.camH * $u
$bodyY = $camY + $camH * 0.24
$lensCx = $camX + $camW / 2; $lensCy = $bodyY + ($camH - $camH * 0.24) / 2 + $camH * 0.02; $lr = $camW * 0.21
$hex = "#{0:X2}{1:X2}{2:X2}"
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 $u $u" width="$u" height="$u">
  <!-- ScreenshotBooth app icon: an outlined window with a camera in its corner. Generated by Tools/Generate-AppIcon.ps1. -->
  <defs>
    <mask id="gap">
      <rect width="$u" height="$u" fill="white"/>
      <g stroke="black" stroke-width="$(F ($s.gap * $u * 2))" stroke-linejoin="round" fill="black">
        <rect x="$(F ($camX + $camW * 0.30))" y="$(F $camY)" width="$(F ($camW * 0.40))" height="$(F ($camH * 0.34))" rx="$(F ($camW * 0.07))"/>
        <rect x="$(F $camX)" y="$(F $bodyY)" width="$(F $camW)" height="$(F ($camH - $camH * 0.24))" rx="$(F ($camW * 0.13))"/>
      </g>
    </mask>
  </defs>
  <g mask="url(#gap)">
    <rect x="$(F ($s.winX * $u + $s.stroke * $u / 2))" y="$(F ($s.winY * $u + $s.stroke * $u / 2))" width="$(F ($s.winW * $u - $s.stroke * $u))" height="$(F ($s.winH * $u - $s.stroke * $u))" rx="$(F ($s.winR * $u - $s.stroke * $u / 2))" fill="none" stroke="$($hex -f $ink.R, $ink.G, $ink.B)" stroke-width="$(F ($s.stroke * $u))"/>
    <path d="M$(F ($s.winX * $u)),$(F ($s.winY * $u + $s.winR * $u)) a$(F ($s.winR * $u)),$(F ($s.winR * $u)) 0 0 1 $(F ($s.winR * $u)),-$(F ($s.winR * $u)) h$(F ($s.winW * $u - 2 * $s.winR * $u)) a$(F ($s.winR * $u)),$(F ($s.winR * $u)) 0 0 1 $(F ($s.winR * $u)),$(F ($s.winR * $u)) v$(F ($s.bar * $u - $s.winR * $u)) h-$(F ($s.winW * $u)) z" fill="$($hex -f $ink.R, $ink.G, $ink.B)"/>
  </g>
  <g fill="$($hex -f $accent.R, $accent.G, $accent.B)">
    <rect x="$(F ($camX + $camW * 0.30))" y="$(F $camY)" width="$(F ($camW * 0.40))" height="$(F ($camH * 0.34))" rx="$(F ($camW * 0.07))"/>
    <path fill-rule="evenodd" d="M$(F ($camX + $camW * 0.13)),$(F $bodyY) h$(F ($camW - 2 * $camW * 0.13)) a$(F ($camW * 0.13)),$(F ($camW * 0.13)) 0 0 1 $(F ($camW * 0.13)),$(F ($camW * 0.13)) v$(F ($camH - $camH * 0.24 - 2 * $camW * 0.13)) a$(F ($camW * 0.13)),$(F ($camW * 0.13)) 0 0 1 -$(F ($camW * 0.13)),$(F ($camW * 0.13)) h-$(F ($camW - 2 * $camW * 0.13)) a$(F ($camW * 0.13)),$(F ($camW * 0.13)) 0 0 1 -$(F ($camW * 0.13)),-$(F ($camW * 0.13)) v-$(F ($camH - $camH * 0.24 - 2 * $camW * 0.13)) a$(F ($camW * 0.13)),$(F ($camW * 0.13)) 0 0 1 $(F ($camW * 0.13)),-$(F ($camW * 0.13)) z M$(F $lensCx),$(F ($lensCy - $lr)) a$(F $lr),$(F $lr) 0 1 0 0.001,0 z"/>
    <circle cx="$(F $lensCx)" cy="$(F $lensCy)" r="$(F ($lr * 0.62))"/>
  </g>
</svg>
"@
[System.IO.File]::WriteAllText((Join-Path $assets "AppIcon.svg"), $svg, (New-Object System.Text.UTF8Encoding $false))
Write-Host "wrote AppIcon.svg"
