#!/usr/bin/env bash
# Qualify the accepted immutable, non-root browser payload without a database or network.
set -euo pipefail
docker run --rm -i --read-only --network none --tmpfs /tmp/wayfarer:uid=1654,gid=1654,mode=0700 --entrypoint sh "$1" -es <<'SHELL'
test "$(id -u)" = 1654
test ! -w /app
test ! -w /opt/wayfarer-browsers
test ! -w /var/lib/wayfarer
test ! -w /var/cache/wayfarer
test ! -w /var/log/wayfarer
test -z "$(dotnet --list-sdks)"
! command -v node
! command -v npm
! command -v pwsh
test -s wwwroot/vite/trip-editor/manifest.json
test -d wwwroot/dist
.playwright/node/linux-x64/node <<'JAVASCRIPT'
const {chromium}=require("./.playwright/package");
(async()=>{
  const browser=await chromium.launch();
  const page=await browser.newPage();
  await page.setContent("<h1>Wayfarer image qualification</h1>");
  await page.pdf({path:"/tmp/wayfarer/probe.pdf"});
  await browser.close();
})().catch(()=>process.exit(1));
JAVASCRIPT
test -s /tmp/wayfarer/probe.pdf
SHELL
