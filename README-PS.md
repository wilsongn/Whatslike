# ChatApp Scaffold — PowerShell only

Use este script para gerar a solução completa **sem Bash/WSL**.

## Passos
1. Abra o **PowerShell** na pasta onde baixou este pacote.
2. Execute:
   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass -Force
   .\New-ChatApp.ps1
   ```
3. Em terminais separados:
   ```powershell
   dotnet run --project ChatApp/Chat.Server -- 5000
   dotnet run --project ChatApp/Chat.Client.Cli -- --host 127.0.0.1 --port 5000 --user alice
   dotnet run --project ChatApp/Chat.Client.Cli -- --user bob
   ```

Se aparecer erro de `dotnet` não encontrado, instale o **.NET SDK 8+**.
