# RunAs Launcher

[![Download](https://img.shields.io/badge/Download-RunAsLauncher--Setup.exe-2ea44f?style=for-the-badge&logo=windows)](https://github.com/brunokdalcastel/runas-launcher/releases/latest/download/RunAsLauncher-Setup.exe)

**Deixe usuários sem permissão de administrador abrirem os programas que precisam de administrador — só os que
você aprovou, sem entregar a senha.**

## Para que serve

Num Windows bem configurado, os usuários do dia a dia são **usuários padrão** (sem direitos de administrador). Isso
protege a máquina, mas cria um problema conhecido de todo suporte de TI: alguns programas legítimos **só funcionam
como administrador** — um sistema contábil antigo, o software de um equipamento, um utilitário de impressora, uma
ferramenta de diagnóstico. Toda vez que o usuário abre um deles, aparece o UAC pedindo uma senha que ele não tem.

As saídas comuns são ruins:

- **Dar a senha de administrador ao usuário** — ele passa a poder instalar e mudar qualquer coisa.
- **Tornar o usuário administrador** — mesmo problema, permanente.
- **O técnico ir até a máquina digitar a senha** — toda vez, para sempre.
- **Ferramentas que guardam a senha de admin num atalho** — a senha fica recuperável por quem tiver o arquivo.

O RunAs Launcher resolve de outro jeito: o administrador monta uma **lista de programas aprovados**, e para cada um
diz **quem** pode abrir e **como** (com direitos de administrador, normal ou restrito). O usuário abre o programa
pelo RunAs Launcher, ou por um atalho na área de trabalho, e ele inicia já com os direitos definidos — **sem UAC,
sem senha, e nenhuma senha guardada em lugar nenhum**. Qualquer programa fora da lista continua pedindo
administrador normalmente.

### Exemplos de uso

- O financeiro precisa abrir o `SistemaFiscal.exe`, que só grava no `C:\Program Files` como administrador.
- O laboratório usa o software do equipamento, que exige admin para acessar a porta USB/serial.
- O usuário pode rodar a ferramenta de diagnóstico de rede da empresa, mas não o Prompt de Comando como admin.

## Como funciona, em uma frase por etapa

1. O **administrador** abre o console *RunAs Launcher — Administração*, arrasta o `.exe` para a lista, escolhe
   quem pode usar e salva. O console registra a "impressão digital" (hash SHA-256) e/ou a assinatura do arquivo.
2. O **usuário** abre o *RunAs Launcher*, vê só os programas liberados para ele e dá dois cliques.
3. O **serviço do RunAs Launcher** (que roda no Windows com privilégio de sistema) confere: quem está pedindo, se
   essa pessoa está autorizada, se o arquivo é exatamente o que foi aprovado e se está numa pasta que usuários comuns
   não conseguem alterar.
4. Se tudo bater, o serviço abre o programa **na tela do usuário, como o próprio usuário**, com os direitos definidos.
   Se algo não bater, nega e explica o motivo.
5. Tudo — aberturas e negações — fica registrado no **Log de Eventos do Windows**, para auditoria.

Se alguém trocar o `.exe` aprovado por outro arquivo, o hash muda e o programa é bloqueado.

### O que ele não é

- **Não é um gerenciador de senhas** nem guarda credenciais: a elevação é feita pelo serviço, sem senha.
- **Não dá administrador ao usuário**: só os programas da lista abrem elevados, e só para quem foi autorizado.
- **Não substitui o cuidado na aprovação**: um programa elevado que permita abrir outros programas (ver *Limites*,
  abaixo) dá ao usuário o mesmo poder de um administrador.

## Instalação rápida

Baixe o `RunAsLauncher-Setup.exe` pelo botão acima e execute como administrador. É um único instalador com tudo.
Como os executáveis não têm assinatura comercial, o Windows pode mostrar "O Windows protegeu o computador":
clique em **Mais informações → Executar assim mesmo**. Detalhes em [Instalação](#instalação).

## Componentes

| Componente | Executa como | Função |
|---|---|---|
| `RunAsLauncher.Service.exe` | serviço Windows, LocalSystem | Lê a política, confere quem pede e o que vai rodar, cria o processo na sessão do usuário e audita |
| `RunAsLauncher.exe` | o próprio usuário (`asInvoker`) | Lista os programas liberados; executa, cria atalho, copia comando (`RunAsLauncher.exe run <id>`) |
| `RunAsLauncher.Admin.exe` | administrador (`requireAdministrator`) | Edita a política, calcula hash/assinatura, importa/exporta, mostra a auditoria |

O launcher nunca envia caminho, argumentos ou linha de comando: só o **id** de um item. Tudo o que é executado vem da
política, gravada em `C:\ProgramData\RunAsLauncher\policy.json`.

## Requisitos

- Windows 10 1809+ ou Windows 11, x64.
- Conta **local** ou de **domínio Active Directory**. Contas **Microsoft Entra ID** (Azure AD) **não são suportadas** —
  o pedido é recusado com uma mensagem clara.
- Para contas de domínio no modo Administrador, um **controlador de domínio acessível** (o logon sem senha usado na
  elevação consulta o DC; offline, o lançamento falha e é auditado).
- Instalação exige administrador.

## Instalação

1. Executar `RunAsLauncher-Setup.exe` como administrador. O instalador:
   - copia os executáveis para `C:\Program Files\RunAs Launcher`;
   - cria `C:\ProgramData\RunAsLauncher` com ACL restrita (SYSTEM e Administradores: controle total; Usuários: leitura);
   - registra a fonte de eventos `RunAsLauncher` e o serviço `RunAsLauncher` (início automático, reinício em falha);
   - cria os atalhos "RunAs Launcher" e "RunAs Launcher — Administração" no menu Iniciar.
2. Atualizar = instalar a versão nova por cima (o serviço é parado e reiniciado; a política é preservada).
3. Desinstalar pelo Painel de Controle; o desinstalador pergunta se a política deve ser apagada.

### Assinatura (opcional, uso privado)

Sem certificado comercial, os executáveis podem ser assinados com um certificado autoassinado:

```powershell
./tools/New-SigningCert.ps1                 # na máquina de build; imprime o thumbprint e gera artifacts/RunAsLauncher-signing.cer
$env:RAL_SIGN_THUMBPRINT = '<thumbprint>'
./publish.ps1
```

Nas máquinas-alvo, importar **só o `.cer`** (como administrador) em `LocalMachine\Root` e
`LocalMachine\TrustedPublisher`. A chave privada nunca sai da máquina de build.

## Como aprovar um programa

1. Abrir **RunAs Launcher — Administração** (pede UAC).
2. Arrastar o `.exe` (ou um `.lnk`) para a lista, ou clicar em **Adicionar**.
3. No editor do item:
   - **Modo**: *Administrador* (elevado), *Padrão* (token normal do usuário) ou *Restrito* (sem privilégios);
   - **Processos filhos**: *Negar* (recomendado), *Permitir* ou *Encerrar todos ao fechar*;
   - **Verificação**: *Calcular hash* (SHA-256 do arquivo atual) e/ou *Ler assinatura* (editor Authenticode);
   - **Exigir pasta protegida**: mantenha ligado — recusa alvos em pastas que um não-administrador possa alterar;
   - **Usuários/grupos**: quem pode usar o item (sem nenhuma conta, ninguém pode).
4. **Salvar**. O serviço recarrega a política sozinho; o launcher mostra o item ao clicar em Atualizar.

Ao atualizar o programa aprovado, recalcule o hash (**Atualizar hash de todos**) — senão ele é bloqueado como
"alterado".

## Modelo de segurança

Cada pedido passa, nesta ordem, por:

1. **Política íntegra** — `policy.json` válido e pasta/arquivo graváveis só por administradores; caso contrário,
   a política inteira é rejeitada e **tudo é negado**.
2. **Autorização** — o SID do chamador (ou um de seus grupos) está nas contas do item. A identidade vem do Windows
   (impersonação do pipe), não do que o cliente declara.
3. **Tipo de conta** — local ou AD; Entra ID é recusada.
4. **Pasta protegida** — o alvo e todas as pastas acima dele não podem ser graváveis por não-administradores.
5. **Arquivo travado e verificado** — o alvo é aberto e mantido aberto (sem escrita compartilhada) até o processo ser
   criado; o caminho real tem de ser o da política (junctions/links são recusados); tamanho, SHA-256 e/ou assinatura
   precisam bater.
6. **Lançamento** na sessão de quem pediu, com o token do modo escolhido e a política de processos filhos.

Outras proteções: pipe com ACL (só usuários autenticados, rede bloqueada) e proteção contra *pipe squatting*;
limite de 20 pedidos por minuto por usuário; argumentos fixos na política.

### Limites — leia antes de aprovar

- **Um programa elevado é um administrador completo.** Qualquer app em modo Administrador que tenha uma caixa de
  diálogo *Abrir/Salvar*, um console, um navegador embutido ou um "executar comando" permite ao usuário abrir outros
  programas — elevados. *Negar processos filhos* reduz isso, mas não transforma um app perigoso em seguro.
- Alvos como `cmd.exe`, `powershell.exe`, `pwsh.exe`, `regedit.exe`, `mmc.exe`, `explorer.exe` e `taskmgr.exe` são
  **equivalentes a dar a senha de administrador** — o console avisa, mas não impede.
- **Aprove só o necessário**, para as contas necessárias, e prefira o modo *Padrão* ou *Restrito* quando a elevação
  não for indispensável.
- O RunAs Launcher não protege contra quem já é administrador da máquina.

## Auditoria

- **Log de Eventos** → *Logs do Windows* → *Aplicativo*, fonte `RunAsLauncher`:

  | ID | Evento |
  |---|---|
  | 1000 | programa lançado (usuário e SID, sessão, item, caminho, SHA-256, modo, PID) |
  | 1001 | pedido negado (com o motivo) |
  | 1002 | erro ao lançar |
  | 1100 / 1101 | política carregada / rejeitada |
  | 1200 / 1201 | serviço iniciado / parado |

- Ou a aba **Auditoria** do console de administração (últimos 500 eventos, exportável em CSV).
- Os eventos podem ser coletados por encaminhamento de eventos do Windows ou pelo SIEM da empresa.

## Desenvolvimento

```powershell
dotnet build RunAsLauncher.slnx -c Release
dotnet test  RunAsLauncher.slnx --filter "Category!=RequiresAdmin"
./publish.ps1        # testes → 3 EXEs single-file em artifacts/publish → artifacts/RunAsLauncher-Setup.exe (Inno Setup 6)
```

Os testes `Category=RequiresAdmin` (`tests/RunAsLauncher.IntegrationTests`) exigem o serviço instalado e um prompt
elevado. O roteiro de testes manuais está em [`docs/Test-Plan.md`](docs/Test-Plan.md); o desenho completo, em
[`docs/PRD.md`](docs/PRD.md) e [`docs/Spec.md`](docs/Spec.md).

## Licença

Proprietária — todos os direitos reservados. Ver [`LICENSE`](LICENSE).
