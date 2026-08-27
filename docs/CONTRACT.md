# Contrato: entrada (FIX XML DataDictionary) → saída (C#)

Status: **Aprovado para v1** (issue #2). Este documento é a referência normativa para as
issues de parser (#4), codegen (#5), diff de schema (#6), testes de conformidade (#7) e
documentação (#8).

Elaborado a partir de propostas independentes de três modelos (Claude Opus 4.8, GPT-5.4,
Gemini 3.1 Pro), consolidadas e resolvidas com o repo owner (@pedrosakuma) nos pontos em
que as propostas divergiam, e validado por pesquisa dedicada de viabilidade técnica
(precedentes reais: Artio/Real Logic, PureFix, EPAM FixAntenna .NET Core, `Utf8JsonReader`).

---

## 0. Posicionamento

Este gerador é o par do [`SbeSourceGenerator`](https://github.com/pedrosakuma/SbeSourceGenerator),
e adota a **mesma premissa central: allocation-free ou, no mínimo, alocação mínima** —
mas o caminho para chegar lá é diferente, porque o domínio é diferente.

- **SBE** é binário, layout fixo → decode é um *overlay* de memória (`ref struct` sobre
  `ReadOnlySpan<byte>` com offsets fixos), O(1), sem parsing algum.
- **FIX tag=value** é texto ASCII, variável em tamanho, delimitado por SOH (`0x01`) →
  não existe overlay possível; decode é necessariamente um *scan* (`8=FIX.4.4␁9=.../35=D␁...`).
  Isso significa que zero-alocação **literal** não é alcançável da mesma forma que no SBE,
  mas alocação **mínima/quase-zero** é, sim, viável — com precedente real e verificado em
  produção (PureFix mede 0 B alocado na tokenização de mensagens FIX reais em C#; Artio,
  do mesmo time do Aeron/SBE, prova que codegen orientado a schema é o que viabiliza
  grupos repetidos zero-alloc, porque os tags delimitadores de grupo ficam conhecidos em
  compile-time no código gerado, eliminando o lookup em dicionário que força alocação em
  engines genéricas como o QuickFIX/n).

**Decisão de arquitetura (premissa central do v1):** a API primária gerada é um par
**reader/writer `ref struct`** sobre `Span<byte>`/`ReadOnlySpan<byte>`, análogo ao
`System.Text.Json.Utf8JsonReader`/`Utf8JsonWriter` — não uma camada de DTOs alocados por
padrão. Decode é *lazy* (campo só é parseado quando acessado), strings são expostas como
`ReadOnlySpan<byte>`/`ReadOnlySpan<char>` por padrão (só alocam `string` se o consumidor
chamar `.ToString()` explicitamente), grupos repetidos são sub-scanners aninhados com
tags delimitadores conhecidos em compile-time (não há lookup em dicionário runtime), e o
encode escreve direto no buffer fornecido pelo chamador com *backpatch* de `BodyLength`
(tag 9) e `CheckSum` (tag 10) via soma corrida — a mesma técnica usada por engines FIX de
baixa latência em produção. Ver §2 para o detalhamento da API gerada.

O formato de entrada suportado é o dicionário **QuickFIX/QuickFIX-J/QuickFIX-n
DataDictionary XML** — o formato de fato usado por engines FIX e gateways de várias
exchanges (raiz `<fix>`, seções `<header>`, `<trailer>`, `<messages>`, `<components>`,
`<fields>`).

## 1. Contrato de entrada (v1)

Suportado:

| Elemento | Atributos | Notas |
|---|---|---|
| `<fix>` | `type?` (`FIX`/`FIXT`), `major`, `minor`, `servicepack?` | Raiz. Ver §7 (versionamento). |
| `<header>` / `<trailer>` | — | Comuns a todas as mensagens do dicionário. |
| `<messages>/<message>` | `name`, `msgtype`, `msgcat?` | Uma classe por mensagem. |
| `<components>/<component>` (definição e referência) | `name`, `required` (na referência) | Reuso recursivo, **não flatten** (§6). |
| `<group>` (aninhado, profundidade ilimitada) | `name`, `required` | Suportado em v1 — dicionários reais (ex. FIX44 público) usam grupos aninhados extensivamente. |
| `<fields>/<field>` (definição) | `number`, `name`, `type` | Tabela global de tipos do dicionário. |
| `<value>` (filho de `<field>`) | `enum`, `description?` | Gera enum C# (§3). |

Fora de escopo / diferido:
- Extensões vendor fora do shape QuickFIX clássico → toleradas com diagnóstico Info, não falham o build.
- Tipo de campo desconhecido → fallback para `string` + diagnóstico Warning (nunca falha o build).
- Validação de range/domínio em runtime além de required/tipo (fast-follow).

### 1.1 Composição FIXT1.1 (transport) + FIX5.0SPx (aplicação)

**Decisão:** o modelo interno (`FixDictionary`) e a API do parser já são desenhados para
suportar merge de um dicionário "transporte" (header/trailer/mensagens admin) com um
dicionário "aplicação" (mensagens de negócio) — ex. `SchemaReader.Parse(appXml, transport:
transportDictionary)`. A **implementação completa** dessa composição (dois arquivos
`AdditionalFiles` relacionados, resolução de namespace do par) fica como fast-follow
depois do v1 single-file, para não inflar o primeiro milestone. Isso evita retrabalho
estrutural: o parser de v1 já devolve um modelo que comporta essa composição sem
mudança de forma.

## 2. Contrato de saída (C#) — API gerada

### 3.1 Decode: reader `ref struct`

Para cada mensagem, o generator emite um `readonly ref struct {Message}Reader` que
recebe um `ReadOnlySpan<byte>` (o corpo da mensagem já isolado do envelope, ou o buffer
completo) e expõe:

- **Propriedades por campo, com localização eager e parsing lazy** — o construtor faz
  um único scan forward-only do buffer e localiza (sem converter) cada campo declarado
  no schema deste nível, guardando `(start, length)` em um par de campos privados
  nomeados por propriedade (não um índice genérico/array/`[InlineArray]`). A
  conversão de tipo (`decimal`/`DateTime`/enum/etc.) só acontece no getter, sob demanda
  — campos nunca lidos nunca pagam o custo de parse, mas todos pagam o custo (barato)
  de localização no scan único do construtor. Ver "Estratégia de leitura" abaixo.
- **Strings como span por padrão:** `ReadOnlySpan<byte> ClOrdIdBytes` /
  `ReadOnlySpan<char>` via decodificação ASCII sem alocação; um método explícito
  `ToClOrdIdString()` (ou propriedade `string ClOrdId`) aloca sob demanda apenas se
  chamado — nunca implicitamente.
- **Campos numéricos/temporais parseados diretamente do span** (`Utf8Parser`,
  `int.TryParse(ReadOnlySpan<byte>)`, `decimal.TryParse(ReadOnlySpan<byte>)` — suportado
  nativamente a partir do .NET 8; como o v1 já assume net6+ no consumidor pelas decisões
  de `DateOnly`/`TimeOnly`, o parsing de `decimal`/`DateOnly`/`TimeOnly` direto de
  `ReadOnlySpan<byte>` sem passar por `string` intermediário é o caminho padrão quando
  disponível no TFM do consumidor; fallback documentado quando não estiver.
- **Grupos repetidos como sub-reader aninhado:** uma propriedade
  `{GroupName}GroupReader GetNoAllocs()` (ou enumerador `foreach`-style, no espírito do
  recurso equivalente do SbeSourceGenerator) que varre o sub-span do grupo. Como o
  generator conhece o schema em compile-time, os **tags delimitadores de cada grupo são
  constantes embutidas no código gerado** — não há lookup em dicionário/schema em
  runtime, o que é exatamente o que torna grupos repetidos zero-alloc viáveis (validado
  pela pesquisa de viabilidade; é a mesma vantagem que o Artio explora via codegen).
- **Componentes como sub-reader aninhado** (não uma cópia de dados) — mesma span,
  apenas uma "view" com o subconjunto de campos do componente.
- **Campos enumerados: `{Field}` (cast direto) + `TryGet{Field}Strict` (validado):** a
  propriedade simples sempre converte o valor numérico/char decodificado para o enum
  gerado, mesmo que esteja fora do domínio `<value>` conhecido pelo schema (produzindo
  um membro "sem nome" com aquele número — comportamento permissivo por padrão, sem
  custo de validação no hot path). Para quem precisa rejeitar valores fora do domínio,
  o reader também expõe `bool TryGet{Field}Strict(out {Enum} value)`, que combina a
  leitura com um `{Enum}.IsDefined()` (extension method emitido junto do enum em
  `{Namespace}.Enums.g.cs`, um `switch` allocation-free sobre os membros conhecidos).
- **Campos `MULTIPLEVALUESTRING`/`MULTIPLECHARVALUE`/`MULTIPLESTRINGVALUE`:** além do span bruto
  do valor completo (`{Field}` ou `TryGet{Field}`), o reader expõe
  `{Field}Values` (tipo `FixMultiValueEnumerator`, `ref struct`), um enumerador forward-only que
  faz split por espaço (ASCII `0x20`) sem copiar/alocar — cada `Current` é uma sub-`ReadOnlySpan<byte>`
  do span original. Compatível com `foreach` diretamente.
- **Índice de tags:** descartado. Após avaliação (issue #12), a estratégia definitiva é
  **localização eager, parsing lazy**: um único scan forward-only no construtor,
  guardando `(start, length)` por campo em campos nomeados individualmente (não um
  array/`[InlineArray]` genérico). Isso evita exigir TFM net8+ (requisito do
  `[InlineArray]`), mantém o reader `readonly ref struct` sem estado mutável
  pós-construção, e dá tamanho de struct proporcional apenas aos campos daquele nível
  (mensagem, componente ou entrada de grupo) — sem capacidade fixa/genérica desperdiçada.
  Grupos repetidos seguem o mesmo padrão por entrada: cada `{Group}EntryReader` faz seu
  próprio scan (delimitado ao sub-span da entrada), sem materializar array de entradas
  (visitadas uma a uma via enumerador forward-only), preservando zero-alloc mesmo para
  grupos com muitas entradas.

```csharp
// Ilustrativo — forma exata definida na issue #5 (codegen)
public readonly ref struct NewOrderSingleReader
{
    private readonly ReadOnlySpan<byte> _buffer;

    public NewOrderSingleReader(ReadOnlySpan<byte> buffer) => _buffer = buffer;

    public ReadOnlySpan<byte> ClOrdIdBytes => FixSpanReader.GetTag(_buffer, tag: 11);
    public string ClOrdId => FixAscii.ToString(ClOrdIdBytes); // aloca só se chamado

    public Side Side => (Side)FixSpanReader.GetChar(_buffer, tag: 54);

    public InstrumentReader Instrument => new(FixSpanReader.GetComponentSpan(_buffer, ComponentTags.Instrument));

    public NoAllocsGroupReader NoAllocs => new(_buffer, groupTag: 78 /* NoAllocs, NUMINGROUP */, entryTags: NoAllocsEntryTags);
}
```

### 3.2 Encode: writer `ref struct` com backpatch

Para cada mensagem, o generator emite um `{Message}Writer` (`ref struct` sobre
`Span<byte>` fornecido pelo chamador) com:

- `BeginMessage(Span<byte> destination)` — escreve `8=FIX.{version}␁`, reserva um campo
  `9=` de largura fixa (placeholder) e `35={MsgType}␁`.
- Métodos `Write{Field}(...)` por campo, na ordem header → body → trailer do schema
  (issue #10), escrevendo `tag=value␁` direto no destino (sem objeto intermediário).
  Campos de `<header>`/`<trailer>` (SenderCompID, TargetCompID, MsgSeqNum, SendingTime,
  Signature etc.) são achatados no mesmo `{Message}Writer`, junto com os campos do
  corpo — não há um writer de header/trailer separado (mesma razão do §"O que isso
  implica" abaixo: `ref struct` não pode compartilhar posição via `ref` field em
  net6+). `BeginString`/`BodyLength`/`MsgType`/`CheckSum` continuam automáticos e nunca
  ganham um `Write{Field}` próprio.
- `Finish()` — faz o *backpatch* do `BodyLength` (tag 9, sobrescrevendo o placeholder
  reservado) e calcula o `CheckSum` (tag 10) por soma corrida dos bytes já escritos,
  igual à técnica usada em produção por engines de baixa latência (ver pesquisa de
  viabilidade, §"Encoding side").
- Grupos: `WriteNoAllocsGroup(int count, Action<...> writeEntry)` ou um builder
  aninhado, sempre escrevendo direto no `Span<byte>` de destino.

### 3.3 O que isso implica (trade-offs assumidos conscientemente)

- **Não há DTO alocado por padrão.** Quem quiser um objeto materializado (para guardar
  em memória além do tempo de vida do buffer, serializar para outro formato, etc.)
  precisa copiar explicitamente os campos que interessam — isso é uma escolha do
  consumidor, não do generator.
- **`ref struct` não pode ser campo de classe, não cruza `await`, não pode ser
  capturado por lambda/closure.** Isso é uma limitação real e conhecida do padrão
  (mesma limitação do `Utf8JsonReader`); vai precisar ser documentada explicitamente
  para os consumidores (issue #8).
- **Alocação não é literalmente zero** em todos os casos: tokenização/scan é O(bytes da
  mensagem) mas 0 B alocado (comprovado por benchmark real do PureFix); parsing de
  `decimal`/data pode alocar dependendo do TFM exato do consumidor (mitigado a partir do
  .NET 8). Ver §10 para os itens que ficam como fast-follow.

## 3. Mapeamento de tipos FIX → C#

| FIX type | C# | Notas |
|---|---|---|
| `STRING`, `CURRENCY`, `EXCHANGE`, `COUNTRY`, `LANGUAGE`, `MONTHYEAR`, `XID`, `XIDREF` | `ReadOnlySpan<byte>` (+ `string` sob demanda via `.ToString()`) | Códigos lexicais ficam como span bruto. |
| `MULTIPLEVALUESTRING`, `MULTIPLECHARVALUE`, `MULTIPLESTRINGVALUE` | `ReadOnlySpan<byte>` (span bruto do valor completo) **+** `{Field}Values` (`FixMultiValueEnumerator`) | Split tipado, allocation-free, sobre a lista delimitada por espaço — ver §2 "Decode". |
| `CHAR` | `char` (ou enum gerado, ver §3) | |
| `INT`, `LENGTH`, `SEQNUM`, `NUMINGROUP`, `DAYOFMONTH`, `TAGNUM` | `int` | Parseado direto do span (`Utf8Parser`/loop de dígitos), sem alocação. |
| `FLOAT`, `PRICE`, `PRICEOFFSET`, `QTY`, `AMT`, `PERCENTAGE` | `decimal` | **Decisão:** `decimal`, não `double` — evita perda de precisão financeira. Consenso dos 3 modelos. Parse direto de `ReadOnlySpan<byte>` sem `string` intermediário (nativo a partir do .NET 8). |
| `BOOLEAN` | `bool` | Wire `Y`/`N`. |
| `UTCTIMESTAMP`, `TZTIMESTAMP` | `DateTime` (UTC, `Kind=Utc`) | **Decisão:** tipado, não `string`. |
| `UTCDATEONLY`/`UTCDATE`, `LOCALMKTDATE` | `DateOnly` | Ver nota de compatibilidade abaixo. |
| `UTCTIMEONLY`, `LOCALMKTTIME`, `TZTIMEONLY`, `TIME` | `TimeOnly` | Idem. |
| `DATA`, `XMLDATA` | `ReadOnlySpan<byte>` | Par típico com campo `LENGTH` precedente; sem cópia — aponta direto para o span de origem. |
| Tipo desconhecido/vendor | `ReadOnlySpan<byte>` + diagnóstico Warning | Nunca falha o build. |

### Nota sobre tipos temporais e TFM

Importante não confundir dois TFMs distintos:
- **TFM do projeto do generator** (`src/FixSourceGenerator`): sempre `netstandard2.0`,
  exigência do próprio Roslyn para componentes de análise/geração de código — igual ao
  SbeSourceGenerator. Isso é inegociável e independente da decisão abaixo.
- **TFM do código gerado**, que roda no projeto consumidor: essa é a decisão de produto.

**Decisão:** o código gerado usa `DateOnly`/`TimeOnly` (nativos, sem polyfill), o que
implica que o **projeto consumidor precisa ser net6+**. Isso é aceito conscientemente —
não há intenção de suportar consumidores netstandard2.0/.NET Framework no v1. Multi-
targeting condicional (`#if NET6_0_OR_GREATER` com fallback `DateTime`/`TimeSpan` para
consumidores legados) fica como fast-follow, caso surja demanda real.

### Campos com `<value>`

Quando um `<field>` tem filhos `<value enum="" description="">`, o tipo do campo vira um
**enum C# gerado** (nome = nome do campo, ex. `Side`), com os membros derivados de
`description` (normalizado para PascalCase). Vale para `CHAR`, `INT` e `STRING` com
`<value>` — o tipo enum substitui o escalar base. Enums são `enum` de valor (`char`/`int`
como backing type conforme o tipo base), nunca alocam.

## 4. Nulabilidade

Regra: `required` é definido **por referência** (dentro de mensagem/componente/grupo),
não na definição global do campo — então a mesma definição de campo pode ser
obrigatória numa mensagem e opcional em outra. Nulabilidade é computada por contexto de
uso, não globalmente.

Como o reader é lazy (não há "ausência de valor" pré-computada — cada acesso verifica se
o tag está presente no span), a convenção é:

| Tipo do campo | `required="Y"` | `required="N"` |
|---|---|---|
| Value type (`int`, `char`, `decimal`, `bool`, `DateOnly`, `TimeOnly`, enum) | propriedade retorna `T` (lança/`Debug.Assert` se ausente — presença garantida pelo schema) | propriedade retorna `T?` (`Nullable<T>`), verifica presença no span sem alocar |
| Span/reference-like (`ReadOnlySpan<byte>`) | span não-vazio garantido pelo schema | propriedade auxiliar `TryGet{Field}(out ReadOnlySpan<byte>)` ou span vazio como sentinela de ausência (a definir em #5) |
| Grupo | sub-reader com `Count > 0` esperado | sub-reader com `Count == 0` quando ausente (não `null` — `ref struct` não pode ser `Nullable<T>` facilmente; ausência = grupo vazio) |
| Componente | sub-reader sempre presente (span pode ser vazio) | idem — presença é responsabilidade do consumidor verificar via campos internos |

Detalhamento fino de "como representar ausência sem alocar e sem exceção no hot path"
fica para a issue #5 (codegen), que deve prototipar as duas abordagens (`Try{Field}` vs.
sentinela) e medir.

## 5. Naming e namespaces

- **Namespace base:** `{Root}.Fix.V{token}` — `{Root}` vem de uma propriedade MSBuild
  (`FixGeneratorNamespace`) com fallback para `RootNamespace`/caminho do arquivo,
  mesma lógica de derivação de namespace do SbeSourceGenerator.
- **Token de versão:** `major`+`minor`+`servicepack` → `V42`, `V44`, `V50SP2`; `type="FIXT"` → `FIXT11`.
  Isso permite múltiplas versões de dicionário coexistirem no mesmo consumidor sem
  colisão de nomes (mesma estratégia de isolamento por schema do SbeSourceGenerator).
- **Mensagem** → `{Name}Reader` / `{Name}Writer` (ex. `NewOrderSingleReader`, `NewOrderSingleWriter`).
- **Componente** → `{Name}Reader` aninhável e reutilizável (ex. `InstrumentReader`) — **não flatten**.
- **Grupo** → tipo aninhado no owner (mensagem/componente/grupo pai); convenção de nome:
  `{GroupName}GroupReader` (ex. `NoAllocsGroupReader`), com enumerador `foreach`-style
  sobre entradas `{GroupName}EntryReader`.
- **Enum de valores** → nome do campo (ex. `Side`), membros em PascalCase a partir de `description`.
- **Colisão com keyword C#:** identificador verbatim (`@event`) ou sufixo `_`.
- **Colisão com tipo BCL** (ex. campo chamado `Boolean`/`String`): gerar código sempre com
  referências totalmente qualificadas (`global::System....`) para tipos do BCL usados
  internamente, e portar um equivalente ao `BclCollisionTests` do SbeSourceGenerator
  desde o início.

## 6. Componentes e grupos repetidos

- **Componentes:** sub-reader/sub-writer aninhado (não uma cópia de dados) sobre o mesmo
  span/buffer da mensagem pai, referenciado por propriedade — **não flatten** no modelo
  de código gerado, ainda que no wire os campos do componente sejam inline (sem
  bracket); o encoder/decoder resolve o achatamento na hora de ler/escrever o buffer.
- **Grupos:** sub-reader/sub-writer com enumerador `foreach`-style (no espírito do
  recurso equivalente do SbeSourceGenerator: "foreach-style zero-allocation enumerator"),
  suportando profundidade ilimitada (grupo dentro de grupo). O campo contador (`NoXxx`,
  `NUMINGROUP`) é lido/escrito diretamente do span — no encode, é derivado da contagem
  real de entradas escritas (backpatch, igual ao `BodyLength`), nunca setado
  manualmente pelo consumidor.
- **Tags delimitadores de grupo conhecidos em compile-time:** o generator embute como
  constantes os tags que compõem cada grupo (baseado no schema), eliminando a
  necessidade de lookup em dicionário/schema em runtime para localizar os limites de
  cada entrada — este é o fator decisivo (validado na pesquisa de viabilidade) que torna
  grupos repetidos praticáveis com alocação mínima.

## 7. Versionamento (ver §1.1 e §5)

Cada dicionário (`AdditionalFiles` XML) gera seu próprio namespace `V{token}` derivado
de `major`/`minor`/`servicepack`/`type`. Múltiplas versões (ex. FIX 4.2 e 4.4) coexistem
no mesmo projeto consumidor sem colisão.

## 8. Diagnostics (parse-time)

| ID | Severidade | Gatilho |
|---|---|---|
| FIX001 | Error | Atributo obrigatório ausente (`name`/`number`/`type` em `<field>`; `name`/`msgtype` em `<message>`; `major`/`minor` em `<fix>`). |
| FIX002 | Error | XML malformado / schema não bem formado. |
| FIX003 | Warning | Construto de schema não suportado (extensão vendor, elemento desconhecido) — tolerado, não falha o build. |
| FIX004 | Error | Definição duplicada no schema (tag/nome de campo duplicado, `msgtype` duplicado, componente duplicado). |
| FIX005 | Error | Referência não resolvida (`<field>`/`<component>`/`<group>` referenciando nome inexistente). |
| FIX006 | Warning | Tipo de campo FIX desconhecido → fallback para `string`. |
| FIX007 | Warning | Grupo sem campo contador `NUMINGROUP` correspondente. |
| FIX008 | Error | Referência circular de componente (A → B → A). |
| FIX009 | Error | Valor de atributo inválido (ex. `number`/`major`/`minor`/`servicepack` não numérico) — antes descartado silenciosamente. |
| FIX010 | Error | `[FixView("Msg")]` não corresponde a nenhuma mensagem carregada. |
| FIX011 | Error | Struct anotada com `[FixView]` não é `partial ref struct`. |
| FIX012 | Error | Propriedade `partial` não corresponde a nenhum campo da mensagem (por nome); inclui sugestão "Did you mean" via distância de Levenshtein. |
| FIX013 | Error | `[FixField("X")]` referencia um campo inexistente na mensagem. |
| FIX014 | Error | Tipo declarado da propriedade incompatível com o tipo FIX do campo — mensagem lista os tipos aceitos. |
| FIX015 | Error | Duas ou mais propriedades da view apontam para o mesmo campo (mesmo tag). |

IDs FIX001–FIX005 já reservados no esqueleto atual do repositório e mantidos
semanticamente compatíveis; FIX006–FIX009 são adições deste contrato; FIX010–FIX015 são do
recurso `[FixView]` (issue #13, ver §11).

## 9. Decisões de escopo confirmadas com o owner

| Pergunta | Decisão |
|---|---|
| Decode-only ou Decode+Encode no v1? | **Decode + Encode já no v1.** |
| Tipos temporais: `string` bruto ou tipado? | **Tipado** (`DateOnly`/`TimeOnly`/`DateTime`, ver nota de TFM acima). |
| TFM mínimo do código **gerado** (consumidor)? | **net6+** (o generator em si continua `netstandard2.0`, exigência do Roslyn) — permite `DateOnly`/`TimeOnly` nativos. |
| Suportar composição FIXT1.1 (transport) + FIX50SPx (app) no v1? | **Modelo do parser já preparado para merge; implementação completa é fast-follow** (ver §1.1) — evita rework estrutural depois sem inflar o v1. |
| API primária: DTO alocado (`class`) ou reader/writer `ref struct` allocation-minimal? | **Reader/writer `ref struct`** sobre `Span`/`ReadOnlySpan<byte>`, sem DTO alocante por padrão — mesma premissa allocation-free do SbeSourceGenerator, adaptada ao domínio texto/variável do FIX. Validado por pesquisa de viabilidade com precedentes reais (Artio, PureFix, EPAM FixAntenna, `Utf8JsonReader`). Ver §2. |
| `decimal` vs `double` para preço/quantidade? | **`decimal`** — consenso das 3 propostas, parseado direto do span sem alocação. |
| Componentes: flatten ou objeto aninhado? | **Sub-reader/sub-writer aninhado, não flatten** — consenso das 3 propostas, adaptado ao modelo span-based. |
| Grupos: representação? | **Sub-reader/sub-writer com enumerador `foreach`-style**, tags delimitadores conhecidos em compile-time, profundidade ilimitada — adaptado do consenso `List<TRow>` original para o modelo allocation-minimal. |

## 10. Itens em aberto (fast-follow, não bloqueiam v1)

- Multi-targeting condicional para emitir `DateOnly`/`TimeOnly` (net6+) vs. `DateTime`/`TimeSpan` (netstandard2.0), caso surja demanda por consumidores legados.
- Implementação completa da composição FIXT1.1 + FIX50SPx (dois arquivos).
- ~~Validação runtime estrita de domínio de enum~~ — **implementado**: `TryGet{Field}Strict` +
  `{Enum}.IsDefined()` (ver §2 "Decode: reader ref struct").
- ~~Parsing tipado de `MULTIPLEVALUESTRING`/`MULTIPLECHARVALUE`~~ — **implementado**:
  `{Field}Values` retorna um `FixMultiValueEnumerator` (forward-only, allocation-free) sobre os
  tokens delimitados por espaço; a propriedade/`TryGet{Field}` do span bruto original é mantida
  (ver §2/§3).
- ~~Prototipar melhor forma de representar "campo ausente" para value types opcionais~~ —
  **implementado**: cada campo opcional guarda um flag `_{campo}Present` além de
  `Start`/`Length`; a propriedade retorna `T?` (ou `TryGet{Field}` para spans),
  distinguindo "ausente" de "presente porém vazio".
- ~~Avaliar necessidade de índice de tags vs. scan direto por campo~~ — **decidido e
  implementado (issue #12)**: localização eager (scan único no construtor, campos
  nomeados `Start`/`Length`/`Present`) + parsing lazy (getter converte sob demanda).
  Descartada a alternativa de índice genérico com `[InlineArray]` (exigiria TFM net8+ e
  quebraria a premissa `readonly ref struct`); ver §2.
- Documentar clara e explicitamente para consumidores as limitações de `ref struct` (não pode ser campo de classe, não cruza `await`, não pode ser capturado por closure) — issue #8.
- Opcionalmente, oferecer uma camada de materialização (DTO alocado) como conveniência **opt-in** para quem precisa reter dados além do tempo de vida do buffer — não bloqueia v1, mas vale registrar como possível fast-follow se houver demanda de ergonomia.

## 11. `[FixView]` — projeção seletiva de campos (issue #13)

Motivação: um reader completo (§2) localiza todos os campos da mensagem no scan do construtor,
mesmo quando o consumidor só lê 2-3 tags de uma mensagem com dezenas/centenas de campos. `[FixView]`
permite declarar, do lado do consumidor, uma `partial ref struct` anotada com os campos de
interesse; o generator casa cada propriedade `partial` com um campo da mensagem-alvo (por nome, ou
por `[FixField("...")]` quando o nome diverge) e emite um construtor de scan único **com
early-exit**: a varredura para assim que todas as N tags pedidas já foram localizadas — diferente
do reader completo (§2), que não pode saber antecipadamente quantos campos possui.

```csharp
using FixSourceGenerator.Attributes;

[FixView("NewOrderSingle")]
public readonly ref partial struct OrderRoutingView
{
    public partial ReadOnlySpan<byte> ClOrdID { get; }
    public partial decimal? Price { get; }

    [FixField("Side")]
    public partial byte RawSide { get; } // escape hatch: valor bruto sem parse do enum

    // Expor um grupo repetido inteiro (issue #17): tipo deve ser exatamente o
    // {Group}GroupReader já emitido para o reader completo desta mensagem.
    public partial NoPartyIDsGroupReader NoPartyIDs { get; }
}

var view = new OrderRoutingView(buffer);
```

Matriz de compatibilidade de tipos (regra do "escape hatch"): toda categoria FIX aceita seu tipo
C# nativo (mesma tabela do §3) **e** `ReadOnlySpan<byte>` como escape hatch bruto/sem parse; campos
enum-eligible (§3) adicionalmente aceitam o tipo enum gerado e seu tipo subjacente (`byte` para
CHAR, `int` para INT). Uma propriedade que casa com um **grupo** (não um campo escalar) deve ter
tipo exatamente `{Group}GroupReader` — sem variantes nullable/span, já que um grupo sempre "existe"
como reader (`Count` pode ser 0 se ausente). Qualquer outro tipo declarado é rejeitado com FIX014
(ver §8).

Grupos como propriedade (issue #17): o construtor de scan early-exit **não** rastreia grupos — a
propriedade apenas envolve o buffer inteiro com `new {Group}GroupReader(_buffer)`, igual ao reader
completo faz. Isso é possível porque `{Group}GroupReader`/`FixGroupEnumerator` já fazem sua própria
busca preguiçosa pelo counter/entradas sob demanda (§2/§6); a view não precisa localizar o grupo
antecipadamente, então ele não conta para o `remaining` do early-exit nem aparece no `switch` da
scan. Campos individuais **dentro** de um grupo continuam fora de escopo — não há valor escalar
único a expor para uma repetição 0..N; use uma segunda `[FixView]` sobre o tipo de entrada gerado
pelo próprio reader completo, se precisar de projeção seletiva também dentro do grupo.

Requisitos e limitações (v1):
- A struct anotada deve ser `partial ref struct` (FIX011) — a implementação armazena um campo
  `ReadOnlySpan<byte> _buffer`, então não pode ser uma struct comum.
- **Exige C# 13 / SDK net9+ do lado do consumidor** (propriedades `partial`), diferente do resto
  do gerador (readers/writers só exigem net6+, §4). Se o consumidor não puder subir para net9+,
  use o reader completo (§2) em vez de `[FixView]`.
- Um `[FixView]` = uma mensagem (`MsgType`); não há views multi-mensagem.
- Campos individuais dentro de um grupo não são "achatados" para dentro de uma view — fora de
  escopo (issue #17 só permite expor o grupo inteiro via seu `{Group}GroupReader`).
- A resolução de tipo é feita por comparação **textual** do tipo declarado (não por
  `ITypeSymbol` resolvido), porque um tipo enum gerado pelo próprio generator nessa mesma
  passagem incremental ainda não existe como metadata resolvível — casar pelo texto evita esse
  problema de auto-referência.
