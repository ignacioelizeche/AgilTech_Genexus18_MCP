# Arquitetura Técnica GeneXus 18 MCP (v1.0.0)

Este documento detalha as soluções de engenharia implementadas para estabilizar a ponte de IA em Knowledge Bases de grande escala.

## 1. Desacoplamento e Configuração Dinâmica

### Problema
O uso de caminhos hardcoded (`C:\Program Files...`) e fallbacks estáticos dificultava a portabilidade e causava erros de carregamento de Assembly em ambientes diferentes.

### Solução (v1.0.0)
- **Zero Hardcoding**: O Worker e scripts de pesquisa resolvem caminhos via `GX_PROGRAM_DIR` e `GX_KB_PATH`.
- **Hot Reload (Configuration Watcher)**: O Gateway utiliza um `FileSystemWatcher` no `config.json`. Alterações salvas pela Nexus-IDE disparam o reinício automático do Worker, garantindo que o MCP sempre aponte para a KB ativa sem intervenção manual.

## 2. Tool Registry Dinâmico (Single Source of Truth)

### Problema
As definições de ferramentas MCP estavam duplicadas em strings C# nos Routers do Gateway, dificultando a manutenção e sincronização com a IDE.

### Solução
- **`tool_definitions.json`**: Todas as ferramentas, descrições e schemas JSON residem em um único arquivo.
- **Roteamento Inteligente**: O Gateway carrega o JSON e delega a execução para o Worker. Isso permite adicionar ferramentas novas apenas editando o JSON, sem necessidade de recompilar a lógica de "discovery" do Gateway.

## 3. Abstração de Objetos

### Problema
Cada serviço implementava sua própria lógica para encontrar partes de objetos (Source, Rules, etc), gerando centenas de linhas de `if/else` e mapeamentos de GUIDs espalhados.

### Solução
- **`PartAccessor.cs`**: Uma camada de abstração que entende a topologia de qualquer objeto GeneXus. Se você pede "Source", o `PartAccessor` sabe se deve buscar no `ProcedurePart` ou no `EventsPart` dependendo do tipo do objeto.
- **Padronização de DTOs (`McpResponse`)**: Todas as respostas do Worker seguem um contrato estrito, facilitando o consumo pela IA.


## 2. Performance: Indexação em Duas Etapas

### Problema
Processar 36.000 objetos, incluindo a resolução de referências (`GetReferences()`), levava horas e travava a interface inicial ("0/0").

### Solução
Dividimos a indexação em fases:
1.  **Fase 1: Gathering (3 segundos)**: Coleta apenas nomes, tipos e metadados básicos. Permite que a árvore de objetos e a busca global funcionem quase instantaneamente.
2.  **Fase 2: Selective Deep Indexing**: Apenas objetos de lógica core (`Procedure`, `Transaction`, `WebPanel`, `DataProvider`) sofrem análise de partes (regras `parm` e snippets).
3.  **Fase 3: Background Reference Crawler**: Um rastreador em segundo plano percorre os objetos core para identificar quem chama quem. O progresso é salvo incrementalmente no cache local (`AppData/Local/GxMcp`).

## 3. Estabilidade: Prevenção de Feedback Loops

### Problema
Ao espelhar arquivos físicos para a pasta `.gx_mirror` (para permitir que a Gemini CLI indexe o código), o Watcher do VS Code detectava a mudança e disparava um comando de salvamento de volta para a KB, criando um loop infinito de `obj.Save()` e `Commit()`.

### Solução
Implementamos um sistema de Mutex lógico:
- **`ignoredPaths`**: A extensão mantém um conjunto temporário de arquivos que ela mesma acabou de gravar. O Watcher ignora qualquer evento de mudança para estes arquivos pelos próximos 2 segundos.
- **Content-Identical Skip**: No Worker, antes de iniciar uma transação pesada de `obj.Save()`, comparamos o código recebido com o código atual na KB. Se forem idênticos, a operação é abortada silenciosamente.

## 4. Physical Mirroring (.gx_mirror)

Para que ferramentas como **Gemini CLI** e **Antigravity** funcionem nativamente, criamos um espelho físico da KB:
- Localizado na raiz do projeto em `.gx_mirror/`.
- Ignorado pelo Git via `.gitignore`.
- Explicitamente incluído para indexação de IA via `.antigravityignore`.
- Atualizado automaticamente sempre que um objeto é aberto ou indexado no Nexus IDE.

---
**Autor**: Gemini CLI (Engineering Task v19.3)  
**Data**: 3 de Março de 2026
