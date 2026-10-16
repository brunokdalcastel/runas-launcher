# Spike de token (Fase 1)

Objetivo: provar que um componente rodando como **LocalSystem** consegue gerar, via logon **S4U (sem senha)**,
um token primário do **próprio usuário** com o grupo `BUILTIN\Administrators` habilitado e **integridade alta**,
e lançar um processo com ele na área de trabalho do usuário.

## Como compilar

```powershell
cd C:\Users\Usuario\runas-launcher
dotnet publish tools\TokenSpike\TokenSpike.csproj -c Release -r win-x64 --self-contained -o artifacts\spike
```

## Como rodar como SYSTEM

O spike **precisa** rodar como LocalSystem (é lá que o `SeTcbPrivilege` existe). Com um **usuário padrão logado
na sessão do console**, abrir um prompt **de administrador** e:

```powershell
$exe = "C:\Users\Usuario\runas-launcher\artifacts\spike\TokenSpike.exe"
# <usuario> = nome do usuário logado na sessão ativa; <dominio> = "." para conta local
schtasks /create /tn RALSpike /sc once /st 00:00 /ru SYSTEM /rl HIGHEST /tr "`"$exe`" <usuario> . local" /f
schtasks /run /tn RALSpike
# aguardar alguns segundos
Get-Content C:\ProgramData\RunAsLauncherSpike\result.txt
schtasks /delete /tn RALSpike /f
```

Alternativa (precisa do Sysinternals): `psexec -s -i <sessionId> "$exe" <usuario> . local`.

## O que observar

- `result.txt`: linha `ElevationType(S4U)=...`, `usouLinkedToken=...`, `forcouIntegridade=...` e `RESULTADO: sucesso`.
- Na **área de trabalho do usuário** deve abrir um `cmd` com `whoami` = o próprio usuário e `whoami /groups`
  mostrando `BUILTIN\Administrators` **Enabled** e `Mandatory Label\High Mandatory Level`.

## Resultados

Executado em 2026-10-16, Windows 11 Pro 26200 (DESKTOP-DL3BPBO), como LocalSystem via tarefa agendada.

| Cenário | ElevationType | LinkedToken usado | Integridade forçada | Resultado |
|---|---|---|---|---|
| Conta local `usuario` (admin local), sessão de console | `Default` | Não | Não | **Sucesso** |
| Conta local padrão pura | _(a testar em VM)_ | | | |
| Conta de domínio AD (DC acessível) | _(a testar em VM)_ | | | |

Token final observado (`whoami /all` em `whoami.txt`):
- Usuário = `desktop-dl3bpbo\usuario` (o **próprio usuário**, não SYSTEM).
- `BUILTIN\Administradores` (S-1-5-32-544) = **Enabled group** (não "deny only").
- Mandatory Label = **Nível Obrigatório Alto / S-1-16-12288** (High).

Conclusão: o logon **S4U + LogonType=Batch + LocalGroups=[Administrators]** devolve um token primário **não
filtrado**, já com Administrators habilitado e integridade High. R1 (token filtrado pelo UAC) **não se
manifestou** — `ElevationType=Default` indica que não houve split token.

## Estratégia escolhida (passos 5/6 do TokenFactory)

Nesta máquina os passos 5 (TokenLinkedToken) e 6 (forçar integridade) **não foram necessários**. Decisão:
**manter os dois como fallback defensivo** no `TokenFactory` — eles só disparam se, em outra máquina/configuração
de UAC, o token S4U vier `Limited` ou com integridade < High. O caminho feliz validado não os aciona.
Reavaliar com uma **conta padrão pura** e uma **conta de domínio** numa VM antes do release (Fase 9 / Test-Plan).

## Observação sobre contas não suportadas

Contas Entra ID (`S-1-12-1-*`) e Microsoft Account não entram na v1. Se o logon S4U falhar para a conta local
vinculada a MSA, o `AccountClassifier` (Fase 3) deve mapear esse caso para "conta não suportada".
