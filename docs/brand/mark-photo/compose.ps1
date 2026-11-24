Add-Type -AssemblyName System.Drawing
$cs = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class Shad2 {
    public static Bitmap MakeShadow(string src, int cr, int cg, int cb, int radius, double opacity) {
        Bitmap b = new Bitmap(src);
        int w = b.Width, h = b.Height;
        double[] a = new double[w*h];
        Rectangle rc = new Rectangle(0,0,w,h);
        BitmapData d = b.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = d.Stride;
        byte[] buf = new byte[stride*h];
        Marshal.Copy(d.Scan0, buf, 0, stride*h);
        for (int y=0;y<h;y++){ int row=y*stride; for(int x=0;x<w;x++){ a[y*w+x] = buf[row+x*4+3]/255.0; } }
        b.UnlockBits(d); b.Dispose();
        for (int pass=0; pass<3; pass++) {
            double[] t = new double[w*h];
            for (int y=0;y<h;y++){ int row=y*w; for(int x=0;x<w;x++){ double s=0; int c=0;
                for(int k=-radius;k<=radius;k++){ int xx=x+k; if(xx>=0&&xx<w){ s+=a[row+xx]; c++; } }
                t[row+x]=s/c; } }
            a = t;
            t = new double[w*h];
            for (int x=0;x<w;x++){ for(int y=0;y<h;y++){ double s=0; int c=0;
                for(int k=-radius;k<=radius;k++){ int yy=y+k; if(yy>=0&&yy<h){ s+=a[yy*w+x]; c++; } }
                t[y*w+x]=s/c; } }
            a = t;
        }
        Bitmap o = new Bitmap(w,h,PixelFormat.Format32bppArgb);
        BitmapData od = o.LockBits(new Rectangle(0,0,w,h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte[] ob = new byte[od.Stride*h];
        int ostride = od.Stride;
        for (int y=0;y<h;y++){ int row=y*ostride; for(int x=0;x<w;x++){ int i=row+x*4;
            double v = a[y*w+x]*opacity; if(v>1)v=1;
            ob[i]=(byte)cb; ob[i+1]=(byte)cg; ob[i+2]=(byte)cr; ob[i+3]=(byte)Math.Round(v*255); } }
        Marshal.Copy(ob,0,od.Scan0,ob.Length); o.UnlockBits(od);
        return o;
    }
}
'@
$refs = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Rectangle].Assembly.Location)
Add-Type -TypeDefinition $cs -ReferencedAssemblies $refs

function Compose([string]$basePath, [int]$dogW, [int]$rightX, [int]$bottomY, [string]$outPath, [int]$shadowRadius) {
  $dir = 'C:\Users\hale\AppData\Local\Temp'
  $dh = [int]($dogW * 1290.0 / 1260.0)
  $dw = $dogW
  $x = $rightX - $dw
  $y = $bottomY - $dh
  $dogSmall = New-Object System.Drawing.Bitmap $dw, $dh
  $gs = [System.Drawing.Graphics]::FromImage($dogSmall)
  $gs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $gs.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $gs.Clear([System.Drawing.Color]::Transparent)
  $big = [System.Drawing.Bitmap]::FromFile("$dir\doghead-cut.png")
  $gs.DrawImage($big, (New-Object System.Drawing.Rectangle(0,0,$dw,$dh)))
  $gs.Dispose(); $big.Dispose()
  $tmp = "$dir\dog-small-$dogW.png"
  $dogSmall.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
  $dogSmall.Dispose()
  $shadow = [Shad2]::MakeShadow($tmp, 5, 24, 44, $shadowRadius, 0.5)
  $sp = "$dir\dog-shadow-$dogW.png"
  $shadow.Save($sp, [System.Drawing.Imaging.ImageFormat]::Png)
  $shadow.Dispose()

  $canvas = New-Object System.Drawing.Bitmap 1024,1024
  $g = [System.Drawing.Graphics]::FromImage($canvas)
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $bmpBase = [System.Drawing.Bitmap]::FromFile($basePath)
  $g.DrawImage($bmpBase, 0, 0)
  $bmpShadow = [System.Drawing.Bitmap]::FromFile($sp)
  $g.DrawImage($bmpShadow, $x, ($y + 9))
  $bmpDog = [System.Drawing.Bitmap]::FromFile($tmp)
  $g.DrawImage($bmpDog, $x, $y)
  $g.Dispose()
  $canvas.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
  $canvas.Dispose(); $bmpBase.Dispose(); $bmpShadow.Dispose(); $bmpDog.Dispose()
  "  -> $outPath  dog($x,$y) ${dw}x${dh}"
}

$dir = 'C:\Users\hale\AppData\Local\Temp'
Compose "$dir\svg-preview\dogbase3.png" 200 866 852 "$dir\svg-preview\dogC.png" 6
Compose "$dir\svg-preview\dogbase3.png" 232 880 880 "$dir\svg-preview\dogD.png" 7

