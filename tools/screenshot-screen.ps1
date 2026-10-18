# Saves a screenshot of the whole main screen (e.g. four test copies in its corners), scaled to -Width.
param([Parameter(Mandatory = $true)][string]$Out, [int]$Width = 2000)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$full = New-Object Drawing.Bitmap $b.Width, $b.Height
$g = [Drawing.Graphics]::FromImage($full)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $full.Size)
$h = [int]($b.Height * $Width / $b.Width)
$small = New-Object Drawing.Bitmap $Width, $h
$g2 = [Drawing.Graphics]::FromImage($small)
$g2.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g2.DrawImage($full, 0, 0, $Width, $h)
$small.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
"Saved $Out"
