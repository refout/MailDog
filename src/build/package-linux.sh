#!/usr/bin/env bash
# Linux 发布与打包：Native AOT 三个可执行文件 + Skia/HarfBuzz 原生库 + maildog 启动脚本。
set -euo pipefail

runtime="${1:-linux-x64}"
version="${2:-}"
configuration="${3:-Release}"

case "$runtime" in
    linux-x64|linux-arm64) ;;
    *) echo "不支持的 Linux RID：$runtime" >&2; exit 1 ;;
esac

src_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo_root="$(cd "$src_root/.." && pwd)"
artifacts="$repo_root/artifacts/$runtime"
app_dir="$repo_root/dist/$runtime/MailDog"

if [[ -z "$version" ]]; then
    version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$src_root/Directory.Build.props" | head -n 1)"
fi

if [[ -z "$version" ]]; then
    echo "无法确定版本号，请传入第二个参数。" >&2
    exit 1
fi

if [[ "$runtime" == "linux-arm64" ]]; then
    echo "linux-arm64 需要 ARM64 交叉编译工具链，当前脚本只保证 linux-x64。" >&2
    exit 1
fi

publish_args=(
    -c "$configuration"
    -r "$runtime"
    -p:PublishAot=true
    -p:PublishSingleFile=true
    -p:IncludeNativeLibrariesForSelfExtract=true
    -p:StripSymbols=true
    -p:Version="$version"
    --nologo
)

echo "MailDog $version ($runtime, $configuration)"

dotnet publish "$src_root/MailDog.Engine/MailDog.Engine.csproj" "${publish_args[@]}" -f net11.0 -o "$artifacts/engine"
dotnet publish "$src_root/MailDog.UI/MailDog.UI.csproj" "${publish_args[@]}" -o "$artifacts/ui"
dotnet publish "$src_root/MailDog.Cli/MailDog.Cli.csproj" "${publish_args[@]}" -o "$artifacts/cli"

rm -rf "$app_dir"
mkdir -p "$app_dir"

cp "$artifacts/engine/MailDog.Engine" "$app_dir/"
cp "$artifacts/ui/MailDog.UI" "$app_dir/"
cp "$artifacts/ui/libSkiaSharp.so" "$app_dir/"
cp "$artifacts/ui/libHarfBuzzSharp.so" "$app_dir/"
cp "$artifacts/cli/MailDog.Cli" "$app_dir/"

cat > "$app_dir/maildog" <<'EOF'
#!/usr/bin/env sh
exec "$(dirname "$0")/MailDog.Cli" "$@"
EOF

chmod +x "$app_dir/maildog" "$app_dir/MailDog.Engine" "$app_dir/MailDog.UI" "$app_dir/MailDog.Cli"

package="$repo_root/dist/MailDog-$version-$runtime.tar.gz"
rm -f "$package"
tar -czf "$package" -C "$app_dir" .

echo "产物：$package"
ls -lh "$package"
