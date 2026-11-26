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

if [[ "$runtime" == "linux-arm64" && "$(uname -m)" != "aarch64" ]]; then
    echo "linux-arm64 需要原生 ARM64 主机（GitHub 的 ubuntu-24.04-arm）；当前是 $(uname -m)。" >&2
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

# 发布包曾出现过 CLI 与引擎版本不一致，导致 Linux 无托盘时 `maildog open` 不可用。
# 打包前直接运行当前 CLI 的 schema，确认无托盘兜底入口确实在包里。
if ! "$app_dir/MailDog.Cli" schema --json | grep -q '"name":"open"'; then
    echo "CLI 包缺少 open 命令，拒绝生成发布包。" >&2
    exit 1
fi

# CLI 不随包展开：把顶层 CLI 与 Skill 收进 cli/ 压缩包，再移除顶层文件。
bundle_dir="$app_dir/cli"
mkdir -p "$bundle_dir"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT
cp "$app_dir/MailDog.Cli" "$staging/MailDog.Cli"
cp "$app_dir/maildog" "$staging/maildog"
chmod +x "$staging/MailDog.Cli" "$staging/maildog"
mkdir -p "$staging/maildog-cli"
cp "$src_root/build/cli-bundle/maildog-cli/SKILL.md" "$staging/maildog-cli/SKILL.md"
cp "$src_root/build/cli-bundle/README.txt" "$staging/README.txt"
tar -czf "$bundle_dir/MailDog-cli.tar.gz" -C "$staging" .
rm -rf "$staging"
trap - EXIT
rm -f "$app_dir/MailDog.Cli" "$app_dir/maildog"

package="$repo_root/dist/MailDog-$version-$runtime.tar.gz"
rm -f "$package"
tar -czf "$package" -C "$app_dir" .

echo "产物：$package"
ls -lh "$package"
