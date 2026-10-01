# Windows UI CI 使用方式

一般程式檢查繼續在 GitHub 提供的 Windows runner 上自動執行。需要桌面及繁中 OCR 的測試另外使用 **手動觸發、專用 Windows 測試機**，避免不同環境造成誤判，也不把私人 LINE 或 API Key 帶進 CI。

## 已交付與尚待連接

- `.github/workflows/ui-smoke.yml`：只允許手動觸發預設分支，使用 `lineintent-ui` 標籤及 `ui-smoke` environment，避免同時操控同一桌面。
- `scripts/Run-UiSmoke.ps1`：先檢查未鎖定桌面、繁中 OCR、字型；環境缺失直接失敗，不默默略過測試。每次使用全新輸出目錄，避免拿到舊報告。
- `scripts/Prepare-UiTestMachine.ps1`：只在明確傳入 `-InstallOcr` 時安裝 OCR／中文字型能力；不會註冊 runner 或讀取 token。
- 目前專案沒有 GitHub 遠端及已註冊測試機，因此尚無 GitHub Actions 執行結果；相同測試腳本已在本機一般桌面完成驗證。

GitHub 支援自託管 runner 的自訂環境，以及 environment 的審核和分支限制。Environment 並不提供電腦隔離，不能把它當成私人電腦的安全沙箱。[Runner 說明](https://docs.github.com/en/actions/concepts/runners/self-hosted-runners)、[Environment 說明](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments)。

## 開源後一次性準備

1. 準備獨立的 Windows 11 測試 VM／測試機及測試帳號，不登入私人 LINE，不放 API Key。公開專案建議使用每次工作後重設的 VM 與 ephemeral runner。
2. 安裝 .NET 10 SDK。在測試機以系統管理員 PowerShell 執行下方準備腳本，安裝繁中 OCR 與字型；若顯示需要重開機，先重開機。
3. 在 GitHub repository 的 Settings → Actions → Runners，依官方產生的 Windows 指令註冊 runner，加上 `lineintent-ui` label。選 ephemeral 模式，保持桌面登入且未鎖定，從桌面執行 `run.cmd`；不要安裝成 Windows service。
4. 建立 `ui-smoke` environment，限制預設分支並設置必要審核者；若組織方案支援 runner group／workflow 限制，將 runner 限定給此 workflow。不要讓公開 PR 自動在私人主機上執行。
5. Actions → **Controlled Windows UI smoke** → Run workflow，選預設分支。工作只上傳合成測試圖、`results.txt` 與環境檢查結果；不會上傳設定檔或密鑰測試檔。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Prepare-UiTestMachine.ps1 -InstallOcr
```

上述註冊需要你自己的 GitHub repository 與 runner 註冊 token；token 只輸入 GitHub 提供的註冊指令，不放進此專案或對話。Ephemeral runner 每次工作後會退出，VM 應重設或重建；單純使用固定 label 並不是隔離措施。

## 本機重跑相同檢查

在 Windows 桌面 PowerShell 執行，無需 API Key，也無需啟動 LINE：

```powershell
dotnet build src/LineIntent/LineIntent.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-UiSmoke.ps1
```

預設輸出為 `artifacts/ui-smoke`，每次執行會建立獨立子目錄，再複製本次安全報告到最外層。腳本有逾時限制，只會停止自己啟動的測試程序，絕不以程序名稱批次關閉其他 LINE／LineIntent。

## 測試範圍

UI smoke 使用合成對話、假 HTTP 回覆與本機 OCR，檢查下拉、匯入、略過空白、草稿重生、快取失效、圖像像素轉換等。它不代表真實模型品質或 LINE 各版本皆相容；真實 LINE 擷取仍另外在授權的本機環境驗證。
