# Plano de testes de cenário — RunAs Launcher v1

Roteiro **manual**, executado numa VM. Cada caso tem passos, resultado esperado e um campo **Resultado** para anotar
`OK` / `FALHOU` + observações (data, build, evento visto). Um caso só passa se o resultado da tela **e** o evento de
auditoria baterem com o esperado.

Referências rápidas:

| Event ID | Significado | | `ResultCode` | Texto no launcher (resumo) |
|---|---|---|---|---|
| 1000 | lançado | | `Denied` | sem permissão |
| 1001 | negado | | `VerificationFailed` | programa alterado, bloqueado |
| 1002 | erro de lançamento | | `UnprotectedLocation` | pasta alterável por qualquer usuário |
| 1100 | política carregada | | `UnsupportedAccount` | conta Entra ID não suportada |
| 1101 | política rejeitada | | `PolicyRejected` | configuração insegura/corrompida |
| 1200 / 1201 | serviço iniciado / parado | | `LaunchFailed` | o Windows não conseguiu iniciar |

Eventos: Visualizador de Eventos → *Logs do Windows* → *Aplicativo*, fonte `RunAsLauncher`; ou a aba **Auditoria** do
console; ou:

```powershell
Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='RunAsLauncher' } -MaxEvents 10 |
    Format-Table TimeCreated, Id, Message -Wrap
```

### Execução automatizada (contas locais)

`tools/run-test-plan.ps1` executa os casos que não dependem de AD, Entra ID, RDP ou cliques. Na VM, como `labadmin`,
num PowerShell **de administrador**, com a pasta do repositório copiada (incluindo `artifacts\RunAsLauncher-Setup.exe`):

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\run-test-plan.ps1
```

O script cria `labuser`/`labuser2`, instala o setup em modo silencioso, grava uma política de teste e, quando pedir,
espera você logar `labuser` por **Trocar usuário** e voltar para a sessão de `labadmin`. Ele grava `C:\RALTest\results.md`
(OK / FALHOU / MANUAL / PULADO por caso) e `C:\RALTest\run.log`. Os itens da política dele têm outros ids/argumentos (deixam
evidência em `C:\RALTest\out`), mas cobrem os mesmos casos. Ao final a instalação fica pronta para os casos MANUAL.
Recusa rodar fora de VM (salvo `-AllowPhysical`); `-SkipUninstall` pula o 7.3.

---

## 0. Preparação da VM

| Item | Detalhe |
|---|---|
| SO | Windows 11 (ou Windows 10 1809+), x64, snapshot limpo antes da instalação |
| Contas | `labadmin` (admin local); `labuser` (usuário local **padrão**); `CORP\aduser` (usuário de domínio padrão); uma conta Entra ID (VM ingressada no Entra, ou segunda VM) |
| Domínio | DC acessível pela VM, com possibilidade de desligá-lo/desconectar a rede para o caso 2.2 |
| Build | `./publish.ps1` na máquina de build → `artifacts/RunAsLauncher-Setup.exe` copiado para a VM |
| Assinatura (opcional) | `.cer` importado em `LocalMachine\Root` e `LocalMachine\TrustedPublisher` (ver `tools/New-SigningCert.ps1`) |

Política de base usada nos casos (criada no console de administração como `labadmin`):

| Id | Alvo | Modo | Filhos | Verificação | Contas |
|---|---|---|---|---|---|
| `cmd` | `C:\Windows\System32\cmd.exe` | Administrador | Permitir | SHA-256 | `labuser`, `CORP\aduser`, conta Entra |
| `notepad` | `C:\Windows\System32\notepad.exe` | Padrão | Negar | SHA-256 | `labuser` |
| `app-teste` | `C:\Program Files\AppTeste\app.exe` (cópia de um EXE qualquer) | Administrador | Encerrar todos ao fechar | SHA-256 | `labuser` |

> `cmd` é alvo de **alto risco** (o console avisa); aqui ele é usado de propósito porque `whoami /groups` prova a elevação.

---

## 1. Instalação e serviço

| # | Passos | Esperado | Resultado |
|---|---|---|---|
| 1.1 | Como `labadmin`, executar o instalador | Serviço `RunAsLauncher` *Em execução*, início automático; evento **1200** | |
| 1.2 | `icacls C:\ProgramData\RunAsLauncher` | Sem herança; `SYSTEM` e `Administradores` **F**; `Usuários` **RX**; nada mais | |
| 1.3 | Abrir "RunAs Launcher — Administração" | Pede UAC; como `labuser` pede credenciais de admin | |
| 1.4 | Criar a política de base (drag & drop do EXE, Calcular hash, contas) e **Salvar** | Evento **1100** com 3 itens, sem reiniciar o serviço | |

## 2. Contas

| # | Passos | Esperado | Resultado |
|---|---|---|---|
| 2.1 | **Usuário local padrão.** Logar como `labuser`, abrir o RunAs Launcher, executar `cmd` | Lista mostra só `cmd`, `notepad`, `app-teste`. `cmd` abre **sem UAC**; `whoami` = `labuser`; `whoami /groups` mostra `BUILTIN\Administradores` *Habilitado* e `Nível obrigatório alto`. Evento **1000** com usuário, SID, sessão, item, caminho, SHA-256, modo e PID | |
| 2.2a | **Usuário de domínio, DC acessível.** Logar como `CORP\aduser`, executar `cmd` | Como 2.1, com `whoami` = `corp\aduser`; evento **1000** | |
| 2.2b | **Usuário de domínio, DC offline.** Com `CORP\aduser` já logado (credencial em cache), desligar o DC / cortar a rede; executar `cmd` | O logon S4U precisa do DC: launcher mostra **"O Windows não conseguiu iniciar o programa."** (`LaunchFailed`), sem travar; evento **1002** com o erro do `LsaLogonUser` (ex.: `NTSTATUS=0xC000005E` — sem servidor de logon). Religar o DC → volta a funcionar | |
| 2.3 | **Conta Entra ID** (SID `S-1-12-1-…`) incluída em `cmd`. Logar com ela e executar `cmd` | **"Sua conta (Microsoft Entra ID) não é suportada…"** (`UnsupportedAccount`); evento **1001**. *Obs.:* se a conta **não** estiver em `AllowedSids`, o item nem aparece na lista e `run cmd` dá `Denied` (a autorização vem antes da classificação da conta) | |
| 2.4 | Usuário fora de `AllowedSids` (`CORP\aduser` em `notepad`): `RunAsLauncher.exe run notepad` | `notepad` não aparece na lista; `run` → **"Você não tem permissão…"** (`Denied`); evento **1001** | |

## 3. Integridade do alvo

| # | Passos | Esperado | Resultado |
|---|---|---|---|
| 3.1 | **Troca do EXE após aprovar.** Como `labadmin`, sobrescrever `C:\Program Files\AppTeste\app.exe` por outro EXE (mesmo nome). Como `labuser`, executar `app-teste` | **"O programa foi alterado desde que o administrador o liberou…"** (`VerificationFailed`); evento **1001** com o SHA-256 calculado (diferente do aprovado). No console, a coluna de hash do item mostra ✖ | |
| 3.2 | Recalcular o hash no console e salvar; executar de novo | Abre; evento **1000** | |
| 3.3 | **Alvo em pasta gravável.** Item apontando para `%TEMP%\app.exe` (ou `C:\Users\Public\app.exe`) com "Exigir pasta protegida" ligado | O console já avisa ao escolher o caminho; executar → **"O programa está numa pasta que qualquer usuário pode alterar…"** (`UnprotectedLocation`); evento **1001** citando o caminho gravável | |
| 3.4 | **Junction para pasta gravável.** Como `labadmin`: aprovar `C:\Program Files\JTeste\app.exe`; depois trocar `C:\Program Files\JTeste` por uma junction (`mklink /J "C:\Program Files\JTeste" C:\Users\Public\JTeste`) contendo `app.exe`. Executar como `labuser` | **Bloqueado**: `UnprotectedLocation` (a pasta real é gravável) ou `VerificationFailed` (o caminho final aberto não é o da política). Nunca abre. Evento **1001** | |
| 3.5 | Mesmo caso 3.4 com a junction apontando para uma pasta **protegida** (`C:\Program Files\Outra`) | `VerificationFailed` — caminho final ≠ caminho aprovado | |
| 3.6 | Durante a execução de 3.2, tentar (como admin) apagar/renomear `app.exe` enquanto o serviço o verifica | Não há janela de troca: o arquivo fica aberto com `FileShare.Read` da verificação até o `CreateProcessAsUser` | |

## 4. Sessões

| # | Passos | Esperado | Resultado |
|---|---|---|---|
| 4.1 | **Múltiplas sessões.** Com `labuser` no console, conectar `CORP\aduser` por RDP (Windows Server/edição que aceite 2 sessões; em Win11 Pro usar *Trocar de usuário*). Na sessão RDP, executar `cmd` | O `cmd` abre **na sessão RDP** (a do chamador), não no console; `query session` / Gerenciador de Tarefas confirmam o ID da sessão; evento **1000** com a sessão certa | |
| 4.2 | Repetir a partir do console | Abre na sessão do console | |
| 4.3 | `runas /user:labuser2 "RunAsLauncher.exe run notepad"` dentro da sessão de `labuser` (item em modo **Padrão**, `labuser2` autorizado) | `Denied` — o usuário interativo da sessão não é o chamador; evento **1001** | |

## 5. Processos filhos

| # | Passos | Esperado | Resultado |
|---|---|---|---|
| 5.1 | **`Children = Deny`.** Executar `notepad`; em *Arquivo → Abrir*, clicar com o direito num `.txt` → *Abrir com* outro app / tentar abrir `cmd.exe` | Nenhum processo filho é criado (falha silenciosa ou erro do Windows) | |
| 5.2 | **`KillTreeOnExit`.** Usar como alvo de `app-teste` um EXE que abra filhos (ex.: cópia de `cmd.exe` com hash recalculado); dentro dele `start notepad`; fechar o processo principal | Ao terminar o principal, os filhos (`notepad`) são encerrados | |
| 5.3 | **`Children = Allow`** (`cmd`): `start notepad`, fechar o `cmd` | `notepad` continua aberto (e elevado — por isso alvos de alto risco são avisados) | |

## 6. Falhas de infraestrutura

| # | Passos | Esperado | Resultado |
|---|---|---|---|
| 6.1 | **Serviço parado.** `net stop RunAsLauncher`; abrir o launcher e usar `run cmd` | Janela abre com lista vazia e aviso **"Serviço RunAs Launcher não está em execução…"**; `run` mostra a mesma mensagem; sem crash. Evento **1201** ao parar | |
| 6.2 | **Política corrompida.** Como `labadmin`, apagar uma `}` do `policy.json` | Evento **1101**; launcher: lista vazia; `run cmd` → **"A configuração… está insegura ou corrompida…"** (`PolicyRejected`). Restaurar o arquivo → **1100** | |
| 6.3 | **Política insegura (ACL).** `icacls C:\ProgramData\RunAsLauncher /grant *S-1-5-32-545:(OI)(CI)M` | Em até 60 s (ou na próxima alteração de arquivo): **1101** e nega tudo. Restaurar a ACL do caso 1.2 → **1100** | |
| 6.4 | Política inexistente (renomear `policy.json`) | **1100** com 0 itens; nada é listado; nada executa | |
| 6.5 | **Rate limit.** Script com 25× `RunAsLauncher.exe run cmd` em < 1 min | Passados 20 pedidos no minuto (todo pedido ao pipe conta, por SID): **"Muitas tentativas…"** (`RateLimited`); volta ao normal após 1 min | |

## 7. Ciclo de vida (instalador)

| # | Passos | Esperado | Resultado |
|---|---|---|---|
| 7.1 | Instalar a mesma versão (ou uma superior) por cima | Serviço parado e reiniciado sozinho; `policy.json` **preservado** | |
| 7.2 | Com o certificado importado: `Get-AuthenticodeSignature "C:\Program Files\RunAs Launcher\*.exe"` | `Valid` nos 3 EXEs | |
| 7.3 | Desinstalar; responder **Não** à pergunta sobre a política | Serviço e fonte de eventos removidos; `C:\ProgramData\RunAsLauncher\policy.json` permanece | |
| 7.4 | Reinstalar e desinstalar respondendo **Sim** | Pasta da política removida | |

## 8. Auditoria e atalhos

| # | Passos | Esperado | Resultado |
|---|---|---|---|
| 8.1 | Console → aba **Auditoria** | Lista os lançamentos/negações dos casos acima (data, usuário, item, resultado); exportar CSV abre no Excel | |
| 8.2 | Launcher → *Criar atalho na área de trabalho* em `cmd`; usar o atalho | Abre `cmd` elevado, sem UAC | |
| 8.3 | *Copiar comando* → colar em Win+R | Idem | |
| 8.4 | Console → Exportar política → Importar em outra VM → Salvar | Mesmos itens; evento **1100** | |

---

## Registro de execução

| Data | Build / versão | VM | Executor | Casos com falha | Observações |
|---|---|---|---|---|---|
| | | | | | |
