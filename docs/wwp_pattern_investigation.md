# Investigacao WWP PatternInstance

## Objetivo

Documentar, com o maximo de precisao possivel, o comportamento observado ao tentar editar `PatternInstance` de objetos WorkWithPlus via MCP, usando o caso real de `ControleExtensaoHoras`.

Este documento registra:

- o que foi testado
- o que foi provado
- o que foi descartado
- os limites atuais do MCP para WorkWithPlus
- a direcao tecnica mais promissora para a proxima fase

## Caso investigado

- Objeto pedido pelo usuario: `ControleExtensaoHoras`
- Objeto autoritativo resolvido pelo MCP: `WorkWithPlusControleExtensaoHoras`
- Tipo resolvido no SDK/KB: `WorkWithPlus`
- Tipo concreto observado em runtime no save/validate: `Artech.Packages.Patterns.Objects.PatternInstance`
- Part investigada: `PatternInstance`

## Problema alvo

Editar via MCP os captions/visibilidade das colunas do grid WorkWithPlus:

- `HorasDebito` -> `Debito (horas devidas)` ou `Débito (horas devidas)`
- `SedCPHor` -> `Horas contabilizadas`
- `SedCPHor.visible` -> `True`

E persistir isso no `PatternInstance` de forma que o WorkWithPlus aceite a mudanca e ela sobreviva a releitura do objeto.

## Descobertas principais

### 1. O MCP le o artefato correto

Foi provado que:

- `ControleExtensaoHoras` resolve para `WorkWithPlusControleExtensaoHoras`
- `genexus_read(part='PatternInstance')` devolve XML real do pattern
- o XML contem os `gridVariable` corretos do grid principal

Nos alvo confirmados:

- `gridVariable name="HorasDebito"`
- `gridVariable name="SedCPHor"`

### 2. O problema nao era transporte HTTP nem timeout

Foi provado que:

- o gateway HTTP responde corretamente para `initialize`, `tools/list`, `genexus_read` e `genexus_edit`
- o timeout original do gateway para metadata XML era um problema real, mas ja foi corrigido
- depois do ajuste de timeout, o write passa a entrar de fato no SDK

### 3. O acento nao e a causa da falha de persistencia

Hipotese levantada:

- `Débito` poderia estar falhando por causa do acento

Resultado:

- testei `HorasDebito` com acento e sem acento
- em ambos os casos a mutacao foi recebida e aplicada em memoria
- a falha final continuou identica

Conclusao:

- o acento nao e o motivo da falha do save do WorkWithPlus

### 4. Havia um bug real de encoding no HTTP para alguns clientes

Sintoma observado:

- em certos probes PowerShell, `Débito` aparecia como `DÃ©bito`

Diagnostico:

- os bytes do gateway ja estavam em UTF-8 correto
- o problema vinha de cliente que inferia charset errado ao ler `application/json` sem `charset=utf-8`

Correcao aplicada:

- o gateway agora responde `application/json; charset=utf-8`

Arquivo ajustado:

- [Program.cs](C:\Projetos\GenexusMCP\src\GxMcp.Gateway\Program.cs)

Conclusao:

- havia um problema de encoding no transporte HTTP para alguns clientes
- isso foi corrigido
- isso nao resolve o save do WWP, mas remove ruido importante de diagnostico

### 5. O campo correto do MCP para escrita e `content`, nao `code`

Foi provado no gateway:

- `genexus_edit` consome `content`
- usar `code` em probes manuais produzia resultados enganosos

Arquivo relevante:

- [ObjectRouter.cs](C:\Projetos\GenexusMCP\src\GxMcp.Gateway\Routers\ObjectRouter.cs)

## Caminhos de escrita testados

### Caminho A: sobrescrever o XML inteiro do `PatternInstance`

Implementacao base:

- ler XML
- montar envelope
- `DeserializeFromXml(...)`
- `EnsureSave(true)` ou save equivalente
- reler e comparar XML persistido

Resultado:

- o save completa
- mas o XML persistido nao volta igual ao XML pedido
- mesmo em no-op, o XML final pode divergir do enviado

Erro observado:

- `Pattern write verification failed`
- detalhe: `The SDK save path completed, but the persisted WorkWithPlus pattern XML does not match the requested content.`

Conclusao:

- regravar o XML inteiro nao e um caminho semantico confiavel para WorkWithPlus

### Caminho B: mutacao nativa na bag de atributos dos `PatternInstanceElement`

Implementacao experimental sob flag:

- `GX_MCP_PATTERN_NATIVE_EXPERIMENT=1`
- localizar `RootElement`
- navegar os elementos alvo
- mutar `Attributes` diretamente

O que funcionou em memoria:

- `HorasDebito.description`
- `HorasDebito.defaultDescription`
- `SedCPHor.description`
- `SedCPHor.defaultDescription`
- `SedCPHor.visible`
- `SedCPHor.defaultVisible`

O que foi provado:

- a mutacao entra
- os valores pedidos chegam corretos
- isso vale com e sem acento

Mas o efeito colateral e grave:

- esse caminho deixa o objeto em estado invalido para o validador do WorkWithPlus

Resultado pratico:

- com mutacao nativa ativada, ate um write no-op passa a falhar no save

Erro observado:

- `Pattern write failed`
- detalhe: `Validation failed for WorkWithPlus 'WorkWithPlusControleExtensaoHoras'`

Conclusao:

- editar a attribute bag nativa diretamente corrompe o estado semantico do `PatternInstance`
- esse caminho nao pode virar a solucao final

## Provas importantes obtidas

### 1. Antes do apply, o objeto e valido

Baseline observado:

- `Validate() = true`
- `ValidateState() = true`
- `ValidateData() = true`
- sem mensagens de erro

### 2. Logo depois do apply, o objeto pode ficar invalido

Com mutacao nativa experimental:

- apos `ApplyPatternEnvelope(...)`, antes do save, o objeto ja fica assim:
  - `Validate() = false`
  - `ValidateState() = true`
  - `ValidateData() = true`
  - sem mensagens

Conclusao:

- a invalidez nasce no modo de apply, nao no save final

### 3. O validador custom do WWP reprova o objeto, mas sem diagnostico util

Foi provado que o part expõe:

- `GetValidator()`
- `GetDataUpdateProcess()`
- `GetDataVersionAdapter()`

No caso real:

- `GetValidator()` retorna um validador custom do WWP
- `validator.Validate(patternObj, output)` retorna `false`
- `output.HasErrors = false`
- sem mensagens
- `GetDataUpdateProcess()` retorna `null`

Conclusao:

- o WWP realmente tem validacao custom
- mas esse validador nao esta entregando mensagem util para o nosso caso

### 4. `ShouldRegenerate()` detecta pendencia, mas nao resolve nada sozinho

Foi observado:

- `ShouldRegenerate() = true` apos a mutacao

Mas:

- chamar `ShouldRegenerate()`
- chamar `LoadInstancePropertyDefinition()`
- chamar `RefreshDefaultDependentParts()`
- chamar `CalculateDefault()`
- chamar `SaveUpdates(false)`
- chamar `SaveUpdates(true)`

Nao faz o objeto voltar a valido nem produz mensagem diagnostica util.

### 5. Existe uma API semantica real do WorkWithPlus para grids

Nova descoberta confirmada por reflexao na DLL:

- `DVelop.Patterns.WorkWithPlus.WorkWithPlusInstance`
- `DVelop.Patterns.WorkWithPlus.WPGridElement`
- `DVelop.Patterns.WorkWithPlus.WPGridAttributeElement`
- `DVelop.Patterns.WorkWithPlus.SettingsGridElement`
- `DVelop.Patterns.WorkWithPlus.SettingsGridWPElement`
- `DVelop.Patterns.WorkWithPlus.SettingsWPGridAttributeElement`

Pontos relevantes dessa API:

- `WPGridElement` expoe:
  - `FindWPGridAttribute`
  - `AddWPGridAttribute`
  - `GridAttributes`
- `WPGridAttributeElement` expoe diretamente:
  - `Name`
  - `Description`
  - `Visible`
  - `Parent`
  - `Element`
- `SettingsGridElement` expoe:
  - `AlwaysUseColumnTitleProperty`
- `SettingsGridWPElement` tambem expoe:
  - `FindWPGridAttribute`
  - `AddWPGridAttribute`

Conclusao:

- o caminho semantico correto do WWP provavelmente passa por esses wrappers de grid
- isso reforca que editar `gridVariable` cru no XML e so uma representacao serializada, nao o modelo autoritativo de alto nivel

### 6. O `WorkWithPlusInstance` pode ser materializado no runtime do worker

Foi validado no worker real, com `GX_MCP_PATTERN_DEBUG=1`:

- `new WorkWithPlusInstance(patternInstance)` funciona
- `Initialize()` no wrapper semantico funciona
- `Settings` tambem existe e pode ser inicializado

Resultado observado:

- `Semantic instance created: DVelop.Patterns.WorkWithPlus.WorkWithPlusInstance`
- `Semantic instance.Initialize()=<null>`
- `Semantic settings object: DVelop.Patterns.WorkWithPlus.WorkWithPlusSettings`
- `Semantic settings.Initialize()=<null>`

Tambem foi observado:

- `Semantic Instance GetAllChildren count=5`
- `Semantic Settings GetAllChildren count=23`

Conclusao:

- o worker ja consegue construir o modelo semantico do WWP sem depender do `PatternEditorHelper`
- isso abre uma trilha mais promissora que XML bruto e attribute bag direta

### 7. O `PatternEditorHelper` continua dependente de host/servico ausente

Mesmo com o modelo semantico disponivel, o helper de editor continua falhando:

- `GetInstanceEditorHelper()` e `GetSettingsEditorHelper()` retornam objetos reais
- `CreateEditors()` falha por servico ausente

Erro observado:

- `Servico nao encontrado: '234dedf1-8a2f-4449-abb8-d594d1a81a79'`

Conclusao:

- o helper de editor continua parecendo depender de host/UI service
- mas a API semantica `WorkWithPlusInstance` pode permitir evitar esse helper

### 8. O estado do no-op mudou de novo na rodada mais recente

Historico consolidado:

- em rodadas anteriores, com certos caminhos experimentais ativos, ate `genexus_edit` no-op do `PatternInstance` falhava
- na rodada mais recente, com a instrumentacao atual e um `genexus_edit` no-op via MCP, o resultado voltou a ser `Success`

Resposta MCP observada:

- `Pattern XML updated and verified.`
- `resolvedObject = WorkWithPlusControleExtensaoHoras`

Estado no log desse no-op:

- `Validation state before apply: isValid=True`
- `Validation state after apply before presave: isValid=True`
- `ValidateState = true`
- `ValidateData = true`
- sem erro antes do save

Conclusao:

- o no-op nao esta mais reproduzindo a falha
- isso reduz o foco do problema para mutacoes semanticas reais de coluna, nao para o path geral de save do `PatternInstance`

Implicacao pratica:

- a pergunta correta agora nao e mais "como salvar qualquer PatternInstance?"
- a pergunta correta passou a ser "qual operacao semantica de grid o WWP exige para aceitar mudanca real de caption/visible?"

## Estado de `IApplyDefaultTarget`

O `PatternInstance` implementa `Artech.Architecture.Common.Defaults.IApplyDefaultTarget`.

Foi instrumentado o seguinte:

- `CurrentProvider`
- `EntityMode`
- `IsCalculatingDefault`
- `IsDefault`
- `IsDefaultCalculated`
- `Dirty`
- `GetProviders()`
- `CanCalculateDefault()`
- `PreserveDefaultLock()`
- `PreserveDefaultUnlock()`

Resultado observado apos apply:

- `Dirty = true`
- `CurrentProvider = null`
- `GetProviders() = []`
- `CanCalculateDefault() = false`
- `IsDefaultCalculated = false`
- `EntityMode = Unchanged`
- `PreserveDefaultLock/Unlock` nao alteram o estado util

Conclusao:

- nao existe provider/default calculavel disponivel nesse contexto
- o problema nao parece ser “faltou calcular defaults”

## Pattern definition

Foi confirmado via `IPatternXPathNavigable`:

- o `RootElement` aponta para `PatternDefinition: WorkWithPlus`

Mas ao refletir a definicao do pattern:

- `Pattern.GetInstanceValidator()` apareceu como ausente
- `Pattern.GetInstanceUpdateProcess()` apareceu como ausente
- `Pattern.GetInstanceVersionAdapter()` apareceu como ausente

Enquanto isso:

- o part continua expondo `GetValidator()`
- o part nao expoe `GetDataUpdateProcess()` util para esse caso, retornando `null`

Conclusao:

- o ponto de extensao util para este caso esta mais proximo do part/engine do que da `PatternDefinition` publica

## O que foi descartado

Itens que deixaram de ser suspeitos principais:

- acento em `Débito`
- erro de roteamento HTTP do MCP
- timeout do gateway como causa principal
- leitura do objeto errado
- falta de acesso ao `PatternInstance`
- payload chegando incorreto ao worker

## O que continua aberto

### 1. Caminho semantico correto para editar WWP

Nem estes caminhos funcionam como solucao final:

- sobrescrever XML inteiro
- mutar bag de atributos

Portanto ainda falta descobrir o caminho autoritativo de edicao do WWP.

### 2. Como aplicar operacoes semanticas do engine de patterns

Os assemblies de patterns indicam uma trilha muito mais promissora:

- `ChangeAttributeValueCommand`
- `PatternInstanceComparer`
- `PatternInstanceMerger`
- `PatternElementPosition`
- `PatternBasePart.AllElements`
- `PatternInstanceElement.ValidateData(...)`
- `IMultipleEditorCommandManager`

Hipotese tecnica atual:

- o WWP espera que a edicao ocorra como delta/operacao semantica do engine de patterns
- nao como reescrita bruta de XML
- nem como set direto de atributo no objeto em memoria

## Melhor explicacao atual do problema

O `PatternInstance` do WorkWithPlus parece manter invariantes internas que nao sao preservadas por:

- `DeserializeFromXml(...)` com XML inteiro vindo do MCP
- mutacao direta da bag `Attributes`

O resultado e:

- o objeto pode ate conter os valores novos
- mas o WorkWithPlus nao o considera semanticamente consistente
- por isso o validador custom retorna `false`
- e o save nativo falha ou normaliza para outro XML

## Direcao recomendada para a proxima fase

Parar de insistir em XML bruto e bag de atributos.

Foco recomendado:

1. localizar como o engine de patterns representa uma alteracao de propriedade
2. construir operacoes semanticas para `HorasDebito` e `SedCPHor`
3. aplicar essas operacoes pelo pipeline do engine
4. reler e validar se o XML persistido passa a refletir a mudanca

Alvos tecnicos prioritarios:

- `Artech.Packages.Patterns.Objects.ChangeAttributeValueCommand`
- `Artech.Packages.Patterns.Engine.PatternInstanceComparer`
- `Artech.Packages.Patterns.Engine.PatternInstanceMerger`
- `PatternBasePart.AllElements`
- APIs de posicao/elemento do engine de pattern

## Descobertas adicionais desta rodada

### 1. `OneSource` e `Sources` nao destravam o caso

Ao refletir o `PatternImplementation` real do WWP:

- `PatternImplementation.GetInstanceOneSource() = null`
- `PatternImplementation.GetInstanceSources() = null`

Conclusao:

- nesta instancia WorkWithPlus, nao existe um `source` semantico exposto por esse caminho
- isso enfraquece a hipotese de que a edicao correta esteja disponivel via `IPatternOneSource` ou `IPatternSources`

### 2. Existe um `EditorHelper`, mas ele depende de um servico ausente

O `PatternImplementation` expoe:

- `GetInstanceEditorHelper()`
- `GetSettingsEditorHelper()`

O helper concreto herda de:

- `Artech.Packages.Patterns.Custom.PatternEditorHelper`

Os membros relevantes observados:

- propriedades:
  - `ReplaceBaseEditor`
  - `BaseEditorCaption`
- metodos:
  - `CreateEditors()`
  - `get_ReplaceBaseEditor()`
  - `get_BaseEditorCaption()`

Ao tentar materializar os editors com `CreateEditors()`:

- a chamada falha com erro de servico:
  - `Servico nao encontrado: '234dedf1-8a2f-4449-abb8-d594d1a81a79'`

Conclusao:

- o helper e uma pista real
- mas o ambiente do worker nao tem todos os servicos registrados para instanciar esses editors fora do host normal da IDE/pattern runtime
- portanto ainda nao foi possivel usar esse caminho para editar semanticamente o grid

### 3. A arvore nativa dos alvos ficou mapeada com precisao

Com a instrumentacao corrigida para usar `Attributes.name`, os alvos foram localizados na arvore real:

- `HorasDebito`
  - no path nativo: `/instance/WPRoot[1]/table/table[2]/table[1]/grid[1]/gridVariable[24]`
- `SedCPHor`
  - no path nativo: `/instance/WPRoot[1]/table/table[2]/table[1]/grid[1]/gridVariable[27]`

Hierarquia ancestral comum:

- `instance`
- `WPRoot`
- `table`
- `table`
- `table`
- `grid`
- `gridVariable`

Conclusao:

- os captions realmente pertencem a `gridVariable` dentro da tabela de grid do `WPRoot`
- nao apareceu nenhuma camada intermediaria de `column` ou metadado visual separado entre o grid e esses nos

### 4. Os nos alvo nao carregam objetos gerados associados

Para ambos os `gridVariable` alvos:

- `PatternInstanceElement.Objects` retornou vazio

Conclusao:

- nao existe, nesse nivel, um objeto derivado associado que explique o caption por fora do bag de atributos
- isso torna menos provavel que o nome visual esteja vindo de um objeto filho gerado anexado ao proprio no

### 5. O no-op tambem falha com o caminho nativo experimental

Com as flags experimentais ativas:

- um `genexus_edit` no-op do mesmo XML do `PatternInstance` volta a cair em:
  - `Pattern write failed`
  - `Validation failed for WorkWithPlus 'WorkWithPlusControleExtensaoHoras'`

Conclusao:

- o simples fato de entrar no caminho nativo experimental ja suja o estado semantico da instancia
- isso confirma que esse caminho nao pode virar solucao padrao

### 6. O erro de validacao continua sendo exclusivamente semantico do WWP

Mesmo nesta rodada, o padrao se manteve:

- `Validate() = false`
- `ValidateState() = true`
- `ValidateData() = true`
- `validator.Validate(...) = false`
- sem mensagens de erro uteis

Conclusao:

- a falha nao e estrutural nem de dados crus
- a invalidade esta numa regra semantica/custom do WorkWithPlus

## Hipotese mais forte agora

Depois desta rodada, a hipotese mais forte mudou levemente:

- ainda parece improvavel que o caminho correto seja XML bruto ou bag de atributos
- `OneSource` e `Sources` nao ajudam neste objeto
- o melhor candidato remanescente e algum fluxo baseado em `PatternEditorHelper`/editors concretos do WWP
- mas esse fluxo depende de servicos que o worker ainda nao consegue resolver fora do host esperado

Em outras palavras:

- a API correta pode ate existir
- mas o MCP/worker ainda nao reproduz o ambiente de servicos necessario para materializar o editor semantico do WWP

## Flags experimentais usadas nesta investigacao

Todas as trilhas experimentais ficaram sob flag, para nao contaminar o comportamento normal:

- `GX_MCP_PATTERN_DEBUG=1`
- `GX_MCP_PATTERN_NATIVE_EXPERIMENT=1`
- `GX_MCP_PATTERN_DELTA_EXPERIMENT=1`
- `GX_MCP_PATTERN_PRESAVE_EXPERIMENT=1`
- `GX_MCP_PATTERN_DIRECT_SAVE_EXPERIMENT=1` em testes anteriores

## Arquivos principais tocados durante a investigacao

- [WriteService.cs](C:\Projetos\GenexusMCP\src\GxMcp.Worker\Services\WriteService.cs)
- [PatternAnalysisService.cs](C:\Projetos\GenexusMCP\src\GxMcp.Worker\Services\PatternAnalysisService.cs)
- [Program.cs](C:\Projetos\GenexusMCP\src\GxMcp.Gateway\Program.cs)

## Estado atual resumido

- leitura do `PatternInstance`: resolvida
- transporte HTTP/charset: resolvido
- validacao de part vazia: resolvido em outra frente
- save semantico de WWP grid metadata: nao resolvido
- caminho XML bruto: insuficiente
- caminho bag de atributos: incorreto
- `OneSource` / `Sources`: nao ajudam neste caso
- `EditorHelper`: promissor, mas bloqueado por dependencia de servico ausente
- proxima fase: entender como satisfazer o `EditorHelper` ou descobrir um hook semantico equivalente que nao dependa do host completo

## Observacao final

Este documento deve continuar sendo atualizado durante a investigacao.

No estado atual, a conclusao mais importante e:

o problema nao esta no texto da coluna, nem no acento, nem no transporte HTTP; o problema esta em descobrir a API/operacao semantica correta que o WorkWithPlus espera para alterar `PatternInstance` sem invalidar a instancia.

## Descoberta nova: o template XLSX carrega os captions do relatorio

Uma verificacao direta no asset do relatorio mostrou que o caption final nao vive apenas no `PatternInstance`.

O arquivo `Desenv/web/RelControleExtensaoHoras01-10-25_16-07-17.xlsx` contem, em `xl/sharedStrings.xml`, os captions exibidos no relatorio:

- `Horas Devidas`
- `Horas Validadas`

Depois de validar a leitura do asset via MCP e inspecionar o workbook extraido localmente, o mapeamento ficou claro:

- `Horas Devidas` corresponde ao caption do debito no relatorio
- `Horas Validadas` corresponde ao caption da coluna contabilizada no relatorio

Eu tambem confirmei que o `genexus_asset` consegue localizar esse arquivo na KB e ler o binario com sucesso:

- `path`: `Desenv/web/RelControleExtensaoHoras01-10-25_16-07-17.xlsx`
- `size`: `51380`
- `mimeType`: `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`

Implicacao pratica:

- a tentativa de corrigir apenas o `PatternInstance` do `ControleExtensaoHoras` nao resolve o texto final do relatorio
- a correcao precisa atingir o template `.xlsx` autoritativo, porque e la que o texto exportado esta armazenado
- o WWP continua relevante para a tela/padrao, mas o export do relatorio tem um segundo artefato de verdade

Conclusao parcial desta frente:

- `PatternInstance` bruto: continua insuficiente para a mutacao desejada
- template `.xlsx`: confirmado como fonte real dos captions do relatorio
- proximo passo tecnico mais provavel: editar o asset do template, nao a instancia XML do pattern

## Publicacao final do template

O template autoritativo foi atualizado com sucesso na KB:

- `Desenv/web/templates/RelControleExtensaoHoras.xlsx`

Captions publicados e verificados:

- `Horas Devidas` -> `Débito (horas devidas)`
- `Horas Validadas` -> `Horas contabilizadas`

Validacao final:

- leitura do asset com `includeContent=true` e `maxBytes=262144` retornou o ZIP completo
- o `sharedStrings.xml` do workbook ativo contem os novos textos
- a escrita do asset passou pela verificacao de hash do MCP

Estado atual desta trilha:

- `PatternInstance` do WWP: ainda importante para a tela, mas nao e o artefato que controla o caption final do relatorio
- template `.xlsx`: corrigido e verificado
- proxima investigacao pendente: se o grid visual da tela ainda precisar de ajuste, isso segue no `PatternInstance` ou na metadata semantica do WWP, separadamente do template do relatorio
