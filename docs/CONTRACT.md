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
`ReadOnlySpan<byte>` por padrão — o generator não emite um `.ToString()`/`{Field}String`
próprio; o consumidor materializa explicitamente com `Encoding.ASCII.GetString(...)`
quando precisar de um `string` (ver USAGE.md §3) —, grupos repetidos são sub-scanners aninhados com
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
- Extensões vendor fora do shape QuickFIX clássico → toleradas com diagnóstico Warning (FIX003), não falham o build.
- Tipo de campo desconhecido → fallback para `ReadOnlySpan<byte>` (span bruto, não `string`) +
  diagnóstico Warning (nunca falha o build).
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
- **Strings como span por padrão:** o campo é exposto diretamente sob o próprio nome
  (ex. `ReadOnlySpan<byte> ClOrdID`, não `ClOrdIdBytes`) — sem `string` intermediário,
  sem materialização implícita. O generator **não** emite um método
  `ToClOrdIdString()`/`ClOrdId` auxiliar; para materializar, o consumidor chama
  explicitamente `Encoding.ASCII.GetString(reader.ClOrdID)` (ver
  [`USAGE.md`](USAGE.md) §3). *(Histórico: `ClOrdIdBytes`/`ToClOrdIdString()` foi a
  proposta original deste parágrafo antes da implementação; nunca foi codificada e não
  reflete o gerador atual — corrigido aqui para casar com `TypeTranslator`/`ReaderEmitter`
  e com os exemplos reais em `examples/ScopedCodec`.)*
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
// Histórico/ilustrativo — proposta original da issue #5 (codegen), anterior à
// implementação. Não reflete o shape gerado hoje: não há sufixo `Bytes` nem método
// `To{Field}String()`/propriedade `string` companheira. Para o shape real, gerado por
// `ReaderEmitter`/`TypeTranslator` e verificado em `examples/ScopedCodec`, ver
// USAGE.md §3 (`ReadOnlySpan<byte> ClOrdID`, materializado por
// `Encoding.ASCII.GetString(reader.ClOrdID)` no lado do consumidor).
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

- Construtor `(destination, state, requiredInputs...)` — inicia o envelope, reserva `9=`
  e escreve os primeiros campos obrigatórios. A metadata tipada tem
  `{Message}Writer.RequiredStateLength` elementos e é inicializada uma vez.
- Fases de mensagem, componente, grupo e entrada recebem os obrigatórios locais por
  construtor/factory, preservando a ordem header → body → trailer. `Write`/`Skip` e
  transições consomem o handle de origem; caudas somente opcionais também oferecem `Set`
  in-place. Headers/trailers não têm owner separado. `BeginString`/`BodyLength`/`MsgType`/
  `CheckSum` continuam automáticos. O contrato completo de ownership está em §12.
- `Finish()` — faz o *backpatch* do `BodyLength` (tag 9, sobrescrevendo o placeholder
  reservado) e calcula o `CheckSum` (tag 10) por soma corrida dos bytes já escritos,
  igual à técnica usada em produção por engines de baixa latência (ver pesquisa de
  viabilidade, §"Encoding side").
- Grupos: `Begin{Group}(expectedCount)`, `BeginEntry(requiredInputs)`, `EndEntry()` e
  `EndGroup()`, sem callbacks alocados nem backpatch de contador de grupo.

**Contrato de capacidade (issue #23):** construtor, setters e `Finish()` falham com
`ArgumentException` (`ParamName == "destination"`) quando falta espaço no destino, inclusive
para separadores, contadores e checksum. A falha invalida a instância: qualquer escrita,
transição ou `Finish()` posterior lança `InvalidOperationException`. O buffer parcial
não deve ser enviado; é necessário construir outro writer. Não há rollback de campos
parcialmente escritos. `Finish()` verifica o espaço para o deslocamento do BodyLength e os
sete bytes do checksum antes de alterar o frame. O destino deve comportar também o estado
intermediário com placeholder de seis dígitos, mesmo quando o frame final usa menos dígitos.
O chamador continua responsável pelo buffer, sem aluguel/crescimento automático e sem
alocações gerenciadas no caminho de sucesso.

**Lifetime de entradas (issue #26):** setters de bytes e `FixSpanWriter.WriteField` /
`BeginMessage` recebem `scoped ReadOnlySpan<byte>` e copiam os bytes imediatamente.
Entradas `stackalloc` podem ser reutilizadas após a chamada, inclusive em helpers com
writer por referência e em campos de grupos. O destino continua retido, sem `scoped`;
readers mantêm os spans que referenciam a entrada. O consumidor usa **net6+ e C# 11+**,
versão de linguagem já necessária para os literais `u8`; não há aumento de TFM.

**Números (issues #25/#31):** campos opcionais da categoria `decimal` têm overloads
`Write`/`Set` para `decimal`, `long` e `(long mantissa, int scale)`. Obrigatórios usam
`FixDecimal`, com conversões implícitas de `decimal`/`long` ou `FromScaled`. A escala aceita
0..18, preservando zeros finais, sinal e os limites de `long`. Escala inválida lança
`ArgumentOutOfRangeException` (`scale`) e invalida o writer antes de escrever o campo.
Os readers permanecem `decimal`; campos `INT`/contadores e identificadores `STRING` não
mudam de tipo. O formatter é compartilhado no runtime, sem string temporária.

**Datas e horas (issue #27):** writers emitem ASCII diretamente, mantendo
`yyyyMMdd-HH:mm:ss.fff`, `yyyyMMdd` e `HH:mm:ss.fff`. Frações abaixo de milissegundos
são truncadas. Como no writer original, `DateTime.Kind` não provoca conversão:
escrevem-se os componentes fornecidos; cabe ao chamador fornecer UTC quando exigido pelo
campo FIX. A interpretação UTC dos readers permanece inalterada. Não há nova precisão,
offset, arredondamento ou alocação gerenciada no caminho de sucesso.

**Prefixos (issue #24):** setters gerados usam literais ASCII constantes (`"270="u8`),
copiados por uma primitiva compartilhada; os formatters de valores são os mesmos da API
dinâmica `WriteField(int tag, ...)`, que permanece disponível. Campos comuns, componentes,
header/trailer e grupos seguem a mesma regra, sem tags específicas de um mercado.
O envelope automático e o algoritmo de backpatch não mudam. A decisão e o custo adicional
de código estão registrados no README dos benchmarks.

### 3.3 O que isso implica (trade-offs assumidos conscientemente)

- **Não há DTO alocado por padrão.** Quem quiser um objeto materializado (para guardar
  em memória além do tempo de vida do buffer, serializar para outro formato, etc.)
  precisa copiar explicitamente os campos que interessam — isso é uma escolha do
  consumidor, não do generator.
- **`ref struct` não pode ser campo de classe, não cruza `await`, não pode ser
  capturado por lambda/closure.** Isso é uma limitação real e conhecida do padrão
  (mesma limitação do `Utf8JsonReader`); as regras de retenção e cópia estão em
  [MIGRATION.md](MIGRATION.md).
- **Alocação não é literalmente zero** em todos os casos: tokenização/scan é O(bytes da
  mensagem) mas 0 B alocado (comprovado por benchmark real do PureFix); parsing de
  `decimal`/data pode alocar dependendo do TFM exato do consumidor (mitigado a partir do
  .NET 8). Ver §10 para os itens que ficam como fast-follow.

## 3. Mapeamento de tipos FIX → C#

| FIX type | C# | Notas |
|---|---|---|
| `STRING`, `CURRENCY`, `EXCHANGE`, `COUNTRY`, `LANGUAGE`, `MONTHYEAR`, `XID`, `XIDREF` | `ReadOnlySpan<byte>` (materialize `string` explicitamente com `Encoding.ASCII.GetString(...)`, sem `.ToString()`/método gerado) | Códigos lexicais ficam como span bruto. |
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

A localização ocorre no construtor; o parse do valor é lazy. A convenção da API não substitui
validação do frame recebido:

| Tipo do campo | `required="Y"` | `required="N"` |
|---|---|---|
| Value type (`int`, `char`, `decimal`, `bool`, `DateOnly`, `TimeOnly`, enum) | propriedade retorna `T`; parsers requeridos usam `Debug.Assert`, não validação estrita em Release | propriedade retorna `T?`; ausência ou falha de parse pode produzir `null` |
| Span/reference-like (`ReadOnlySpan<byte>`) | propriedade retorna span, que pode estar vazio em entrada inválida | `TryGet{Field}(out ReadOnlySpan<byte>)` distingue ausência de valor explicitamente vazio |
| Grupo | contador requerido não implica contagem positiva | `Count == 0` não distingue ausência de contador zero; não há `TryGetCount` gerado |
| Componente | sub-reader sempre presente (span pode ser vazio) | idem — presença é responsabilidade do consumidor verificar via campos internos |

O writer exige presença estrutural via fases e rejeita texto explicitamente vazio, inclusive
opcional; zero/false são valores, não sentinelas de ausência. Views consideram também a
opcionalidade dos componentes ancestrais (§11). Ver [MIGRATION.md](MIGRATION.md).

## 5. Naming e namespaces

- **Namespace base:** `{Root}.Fix.V{token}` — `{Root}` vem de uma propriedade MSBuild
  (`FixGeneratorNamespace`) com fallback para `RootNamespace`/caminho do arquivo,
  mesma lógica de derivação de namespace do SbeSourceGenerator.
- **Token de versão:** `major`+`minor`+`servicepack` → `V42`, `V44`, `V50SP2`; `type="FIXT"` → `FIXT11`.
  Isso permite múltiplas versões de dicionário coexistirem no mesmo consumidor sem
  colisão de nomes (mesma estratégia de isolamento por schema do SbeSourceGenerator).
- **Mensagem** → `{Name}Reader` / `{Name}Writer` (ex. `NewOrderSingleReader`, `NewOrderSingleWriter`).
- **Componente** → `{Name}Reader` aninhável e reutilizável (ex. `InstrumentReader`) — **não flatten**.
- **Grupo** → propriedade no owner (mensagem/componente/grupo pai), com tipos no namespace do schema; convenção de nome:
  `{GroupName}GroupReader` (ex. `NoAllocsGroupReader`), com enumerador `foreach`-style
  sobre entradas `{GroupName}EntryReader`.
- **Enum de valores** → nome do campo (ex. `Side`), membros em PascalCase a partir de `description`.
- **Colisão com keyword C#:** identificador verbatim (`@event`) ou sufixo `_`.
- **Colisão com tipo BCL** (ex. campo chamado `Boolean`/`String`): gerar código sempre com
  referências totalmente qualificadas (`global::System....`) para tipos do BCL usados
  internamente, e portar um equivalente ao `BclCollisionTests` do SbeSourceGenerator
  desde o início.

## 6. Componentes e grupos repetidos

- **Componentes:** sub-reader por propriedade e sub-writer por transição `Begin`/`End`
  sobre o mesmo span/buffer da mensagem pai — **não flatten** no modelo
  de código gerado, ainda que no wire os campos do componente sejam inline (sem
  bracket); o encoder/decoder resolve o achatamento na hora de ler/escrever o buffer.
- **Grupos:** readers têm enumerador `foreach`-style; writers têm fases de grupo/entrada.
  O aninhamento permitido pelo schema determina a metadata necessária. O chamador fornece
  `expectedCount`; o writer escreve o contador uma vez, conta somente entradas fechadas e
  exige igualdade no fechamento. Não há backpatch de grupo, diferentemente de `BodyLength`.
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
| FIX006 | Warning | Tipo de campo FIX desconhecido → fallback para `ReadOnlySpan<byte>` (span bruto, não `string`). |
| FIX007 | Warning | Grupo sem campo contador `NUMINGROUP` correspondente. |
| FIX008 | Error | Referência circular de componente (A → B → A). |
| FIX009 | Error | Valor de atributo inválido (ex. `number`/`major`/`minor`/`servicepack` não numérico) — antes descartado silenciosamente. |
| FIX010 | Error | `[FixView("Msg")]` não corresponde a nenhuma mensagem carregada. |
| FIX011 | Error | Struct anotada com `[FixView]` não é `partial ref struct`. |
| FIX012 | Error | Propriedade `partial` não corresponde a nenhum campo da mensagem (por nome); inclui sugestão "Did you mean" via distância de Levenshtein. |
| FIX013 | Error | `[FixField("X")]` referencia um campo inexistente na mensagem. |
| FIX014 | Error | Tipo declarado da propriedade incompatível com o tipo FIX do campo — mensagem lista os tipos aceitos. |
| FIX015 | Error | Duas ou mais propriedades da view apontam para o mesmo campo (mesmo tag). |
| FIX016 | Error | Alvo de view resolve para múltiplas mensagens/componentes/grupos; qualifique o caminho dentro de um schema ou isole schemas que contêm o mesmo caminho. |

IDs FIX001–FIX005 já reservados no esqueleto atual do repositório e mantidos
semanticamente compatíveis; FIX006–FIX009 são adições deste contrato; FIX010–FIX015 são do
recurso `[FixView]` (issue #13, ver §11); FIX016 cobre ambiguidade de escopo (#32).

## 9. Decisões de escopo confirmadas com o owner

| Pergunta | Decisão |
|---|---|
| Decode-only ou Decode+Encode no v1? | **Decode + Encode já no v1.** |
| Tipos temporais: `string` bruto ou tipado? | **Tipado** (`DateOnly`/`TimeOnly`/`DateTime`, ver nota de TFM acima). |
| TFM mínimo do código **gerado** (consumidor)? | **net6+/C#11** para readers/writers; **net9+/C#13** para `[FixView]`. O generator continua `netstandard2.0`. |
| Suportar composição FIXT1.1 (transport) + FIX50SPx (app) no v1? | **Modelo do parser já preparado para merge; implementação completa é fast-follow** (ver §1.1) — evita rework estrutural depois sem inflar o v1. |
| API primária: DTO alocado (`class`) ou reader/writer `ref struct` allocation-minimal? | **Reader/writer `ref struct`** sobre `Span`/`ReadOnlySpan<byte>`, sem DTO alocante por padrão — mesma premissa allocation-free do SbeSourceGenerator, adaptada ao domínio texto/variável do FIX. Validado por pesquisa de viabilidade com precedentes reais (Artio, PureFix, EPAM FixAntenna, `Utf8JsonReader`). Ver §2. |
| `decimal` vs `double` para preço/quantidade? | **`decimal`** — consenso das 3 propostas, parseado direto do span sem alocação. |
| Componentes: flatten ou objeto aninhado? | **Sub-reader/sub-writer aninhado, não flatten** — consenso das 3 propostas, adaptado ao modelo span-based. |
| Grupos: representação? | Reader com enumerador `foreach`; writer com fases e `expectedCount`, limitadas pela topologia do schema. Delimitadores conhecidos em compile-time. |

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
- ~~Documentar limitações de lifetime de `ref struct`~~ — **documentado** em USAGE/MIGRATION,
  com exemplo executável de buffers emprestados e cópia imediata de inputs do writer.
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
using System;
using FixSourceGenerator.Attributes;
using Acme.Fix.V44;

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

Grupos como propriedade: a view localiza o counter e delimita as entradas no escopo correto,
armazenando dois offsets por grupo solicitado. O grupo participa do early-exit; o getter envolve
apenas esse trecho com `{Group}GroupReader`, não o buffer inteiro. Isso evita localizar um counter
homônimo dentro de outro grupo. Essa localização tem custo, além da enumeração posterior, e deve
ser incluída nas medições. Campos individuais de uma repetição 0..N não são escalares da mensagem;
use uma segunda `[FixView]` direcionada ao escopo da entrada.

Escopo além de mensagem (issue #32): um nome simples é resolvido na ordem mensagem, componente,
grupo. Dentro da categoria escolhida, todas as ocorrências em todos os schemas são consideradas:
mensagens/componentes/grupos repetidos entre schemas ou ocorrências são ambíguos, sem selecionar
silenciosamente o primeiro arquivo. Caminhos pontuados distinguem ocorrências e
preservam a propriedade lógica, por exemplo
`MarketDataIncrementalRefresh.MDIncGrp.NoMDEntries` e
`MarketDataSnapshotFullRefresh.MDFullGrp.NoMDEntries`. Cada segmento depois da raiz deve ser um
componente ou grupo diretamente pertencente ao escopo anterior.
Se o mesmo caminho resolver em mais de um schema carregado, FIX016 continua sendo emitido;
esta forma de qualificação não seleciona uma versão de schema.

`Enumerator.CurrentSpan` expõe o span já delimitado da entrada para construir a view seletiva sem
construir e escanear o entry reader completo:

Quando tags aninhadas reutilizam o delimitador do pai, o enumerador gerado fornece ao runtime
um callback estático de delimitação por topologia. Isso evita cortar a entrada no delimitador
do filho; o callback é compartilhado por tipo, não criado por entrada. Os construtores existentes
do enumerador de runtime sem metadata de topologia mantêm seu comportamento anterior.

```csharp
foreach (var groupReader in message.NoPartyIDs) { } // reader completo, se precisar de tudo

var enumerator = message.NoPartyIDs.GetEnumerator();
while (enumerator.MoveNext())
{
    var partyIdOnly = new PartyIdOnlyView(enumerator.CurrentSpan); // só localiza PartyID
}
```

**Ambiguidade (FIX016):** mais de uma mensagem/componente/grupo elegível para o alvo produz
`FIX016`, independentemente de as formas serem iguais ou da ordem dos arquivos. O diagnóstico
lista namespaces de versão e caminhos candidatos. Use um caminho qualificado quando ele for
único; se o mesmo caminho existir em vários schemas, carregue-os em projetos separados.
O generator não seleciona nem une ocorrências por nome.

Ao escanear um escopo, counters de grupos filhos estabelecem regiões aninhadas. A view pula a
quantidade declarada usando delimitador, membership e a topologia recursiva do schema antes de
continuar o scan do pai. Counts negativos, curtos, excessivos ou sem delimitador encerram o scan
com segurança; uma tag desconhecida fora da membership estabelece o limite normal do grupo.

**Membership residual limitada (#33):** o enumerador gerado mantém busca linear até 16 tags.
Acima disso, usa bitmap somente quando o payload, em palavras de 32 bits, não excede 8 KiB
nem o tamanho do array de tags que substitui; conjuntos esparsos ou com tags altas usam busca
binária. A decisão ocorre na geração, sem construir estruturas por mensagem/entrada e sem
reter simultaneamente array de tags e bitmap. O construtor público que aceita tags não
ordenadas mantém a semântica anterior. Delimitadores e helpers de topologia aninhada não mudam.

**Helpers de skip compartilhados entre views (issue #32, follow-up de tamanho de IL):** o método
que pula um grupo aninhado (`TrySkip{GroupId}`, acima) não é emitido como cópia privada dentro de
cada `[FixView]` — ele é gerado **uma única vez por schema e por topologia de grupo** em um
container `internal static class FixViewGroupSkipHelpers`, no namespace de runtime do schema (ex.:
`Acme.Fix.V50SP2.Runtime.FixViewGroupSkipHelpers`), evitando colisão com nomes de mensagens
ou componentes FIX. Toda view que precisa pular aquele grupo apenas
chama `{Schema}.Runtime.FixViewGroupSkipHelpers.TrySkip{GroupId}(...)`. A identidade de cache é a
referência do `FixGroupRef` já resolvido para aquele schema (não o nome curto do grupo): dois
schemas carregados na mesma compilação nunca compartilham um helper mesmo quando declaram grupos
com o mesmo nome curto (ex.: `NoMDEntries` em `MDIncGrp` vs. em `MDFullGrp`), porque cada um
resolve para uma instância de `FixGroupRef` distinta, ancorada ao namespace do seu próprio schema.
Sem essa consolidação, o escopo de entrada X do FIX50SP2 (dezenas de componentes/grupos aninhados
sob `NoMDEntries`) chegava a ~96KB de IL duplicada **por view** que projetasse esse escopo; com o
container compartilhado, cada view carrega apenas o código de chamada (algumas centenas de bytes),
e o corpo dos helpers é pago uma única vez, reaproveitado por todas as views (e, potencialmente, por
múltiplas ocorrências do mesmo grupo) que compartilham a mesma topologia.

**Primeira ocorrência vence em duplicatas (early-exit):** a exemplo do reader completo (que sempre
usa a *última* ocorrência de uma tag duplicada, §2), a view com early-exit usa a **primeira**
ocorrência dentro do escopo aplicável — uma tag duplicada antes do fechamento do early-exit não
deve impedir localizar os campos restantes solicitados, nem sobrescrever o valor já localizado.
Isso não é validação estrita de FIX (duplicatas continuam sendo aceitas silenciosamente); é
apenas a semântica de localização usada pela projeção.

Campos dentro de componentes opcionais são opcionais no contexto, mesmo quando `required="Y"` na
definição do componente. Para spans opcionais, a propriedade continua retornando span vazio como
conveniência e `TryGet{Property}(out ReadOnlySpan<byte>)` distingue ausência de valor presente e
explicitamente vazio.

Requisitos e limitações (v1):
- A struct anotada deve ser `partial ref struct` (FIX011) — a implementação armazena um campo
  `ReadOnlySpan<byte> _buffer`, então não pode ser uma struct comum.
- **Exige C# 13 / SDK net9+ do lado do consumidor** (propriedades `partial`), diferente do resto
  do gerador (readers/writers só exigem net6+, §4). Se o consumidor não puder subir para net9+,
  use o reader completo (§2) em vez de `[FixView]`.
- Um `[FixView]` = um escopo só (mensagem, componente OU grupo, issue #32); não há views que
  combinem múltiplos escopos.
- Campos individuais dentro de um grupo não são "achatados" para dentro de uma view de
  **mensagem** — fora de escopo (issue #17 só permite expor o grupo inteiro via seu
  `{Group}GroupReader` a partir de uma view de mensagem). Para projetar campos de dentro de um
  grupo, aponte o `[FixView]` diretamente para o nome do grupo (ver acima, issue #32).
- Tipos já resolvidos usam identidade semântica. Enums/readers de grupo emitidos nesta passagem
  ainda não existem na compilação de entrada; nesses casos, o generator combina o nome textual
  com namespaces/imports/aliases do consumidor e compara com o tipo do schema selecionado.
  `using`, nomes qualificados e aliases são aceitos, mas um tipo homônimo de outro namespace não.
  As implementações usam nomes totalmente qualificados, sem depender dos imports do consumidor.

## 12. Contrato de API `ref struct` por escopo (issues #29/#31, writer implementado)

> **Status atualizado por #31:** o writer gerado implementa required-by-scope, fases explícitas,
> metadata tipada caller-owned, epoch compartilhado, poison e grupos com `expectedCount`.
> #32 implementa views seletivas de mensagem/componente/entrada descritas em §11; o reader
> completo mantém seu contrato anterior. As APIs `Proto*` abaixo são histórico, não API pública.
> A implementação escolheu somente contagem antecipada: `Begin{Group}(expectedCount)`, excesso
> falha em `BeginEntry`, falta falha em `EndGroup`, e somente `EndEntry` incrementa a contagem.
> Não há variante de backpatch de grupo até medição e decisão separadas. O chamador deve alocar
> `Span<FixWriterState>[{Message}Writer.RequiredStateLength]`, chamar `InitializeState` uma vez
> para aquela região, e mantê-la viva, exclusiva e sem sobreposição com o destino. Reaquisição
> após sucesso/falha avança uma geração monotônica de 64 bits; geração esgotada não reinicia.
>
> **Histórico de design:** os exemplos em
> `tests/FixSourceGenerator.Tests/ScopedApiContractExamples.cs` +
> `ScopedApiContractExamplesTests.cs` — exemplos limitados que compilam e operam sobre fragmentos
> de corpo, sem envelope FIX nem validação completa do protocolo e sem depender do generator —
> permanecem apenas como registro dos protótipos. `WriterEmitter.cs` agora implementa o contrato
> escopado integrado. O reader completo continua com eager-scan; as views de #32 são a
> implementação seletiva, não os tipos ilustrativos `Proto*`.

### 12.1 Onde vivem os inputs obrigatórios (required-by-scope, não um construtor gigante)

**Implementado:** cada **escopo** (mensagem, componente, entrada de grupo) expõe seus próprios campos
obrigatórios como parâmetros do construtor/factory *daquele* escopo — nunca como parâmetro
transitivo de um construtor de mensagem que também carregasse os campos obrigatórios de
componentes/grupos aninhados. Concretamente:

- O construtor da mensagem recebe apenas os campos obrigatórios *da própria mensagem* que vêm
  antes do primeiro sub-escopo obrigatório no wire (ex.: `ClOrdID`).
- Entrar num componente/grupo obrigatório é uma chamada `Begin{Scope}(...)` que já exige, como
  parâmetros, os campos obrigatórios *daquele* sub-escopo (ex.: `BeginInstrument(symbol)` — não
  existe `InstrumentWriter` "vazio" que ainda precise de `Symbol` depois).
- Fechar o sub-escopo (`End{Scope}(...)`) pode, na mesma chamada, já receber a próxima rajada de
  campos obrigatórios da mensagem-pai que vêm em seguida no wire (`EndInstrument(side, orderQty,
  ordType)`) — resolvendo a intercalação de campos obrigatórios/opcionais descrita em §12.5 sem
  expor três chamadas independentes que o chamador poderia invocar fora de ordem.
- Cada `Begin{Scope}`/`End{Scope}` retorna um **tipo C# diferente** (typestate) que só expõe os
  membros válidos para a fase seguinte — o compilador rejeita a maior parte do uso incorreto
  (ex.: escrever um campo opcional do corpo antes de fechar o componente obrigatório) só por
  não existir tal método no tipo da fase atual.

Trade-off consciente: isso significa **mais tipos gerados** que um writer único e achatado.
Para limitar explosão em schemas grandes, caudas somente opcionais compartilham uma fase e usam
o ordinal runtime descrito em §12.5. O compilador restringe as demais transições; validade dos
valores, cópias e restrições não representadas nas fases exigem runtime.

**Compartilhamento de schema:** cada definição de componente e cada definição distinta de grupo
gera um template `ref struct` genérico uma única vez por schema. O argumento genérico é sempre
um marker struct comum (nunca outro `ref struct`) que identifica a continuação concreta. Writers
raiz continuam não genéricos; callsites dentro de templates usam markers genéricos no marker
externo. `End{Component}`/`EndGroup` são extensions por valor em uma classe compartilhada:
o marker do receiver seleciona o tipo pai, e `EndScope()` renova o epoch antes de devolver o
contexto. Assim, expressões fluentes continuam válidas e a cópia usada pela extension invalida
o receiver original e seus aliases. Grupos homônimos com estruturas diferentes preservam
identidades/templates separados.

Essa fatoração elimina a reemissão recursiva do mesmo grafo em cada mensagem/caminho.
FIX44 e FIX50SP2 completos agora compilam sob o mesmo limite de heap gerenciado de 2 GiB que
rejeitava o primeiro candidato; consumidores net6/C#11 continuam suportados. O custo de escrita
completa ainda pode superar o raw writer com menos garantias (ver benchmark README);
in-place reduz parte desse custo, sem promessa de ganho universal.

O runtime invalida a geração do handle consumido, sem zerar todo o contexto. Antes de uma
escrita, marca o estado compartilhado como falho e avança a geração; somente retorno normal
restaura o estado ativo. Assim qualquer exceção propaga e mantém todos os handles envenenados,
sem rollback nem handlers por campo. Guards válidos deixam a validação de ownership para a
mutação seguinte; guards inválidos validam o handle antes de envenenar, preservando o dono ativo
quando o uso incorreto veio de uma cópia obsoleta.

### 12.2 Omissão vs. zero vs. `false` vs. span vazio explícito

**Atual:** já resolvido e implementado para o reader (§4/§2.1) — campo opcional escalar retorna
`T?`, campo opcional span expõe `TryGet{Field}(out ReadOnlySpan<byte>)`; ambos distinguem
"ausente" de "presente porém vazio/zero/false" via um flag `_{campo}Present` guardado à parte do
`Start`/`Length`. **Isso não muda** — o spike só estende a mesma distinção para o *writer* e para
grupos:

- **Implementado (campo escalar/span opcional):** chamar `Skip{Field}` em vez de `Write{Field}`/
  `WriteSecurityID` correspondente — omissão é "nunca escrever o tag", não "escrever um valor
  sentinela". Chamar com `0`/`false` escreve o valor literal quando permitido pela regra do
  campo. Um valor inválido, inclusive vazio, deve falhar explicitamente, nunca virar omissão.
- **Validação não é inferida só de `required="Y"`:** parâmetro obrigatório garante presença da
  chamada, não validade do valor. Campos lexicais requeridos cuja regra aplicável proíbe vazio
  lançam `ArgumentException`; enums/códigos e ranges explicitamente conhecidos lançam
  `ArgumentOutOfRangeException`. A proposta rejeita texto vazio por padrão, mesmo opcional;
  qualquer exceção exige uma regra explícita do campo, não apenas `required="N"`.
  O spike rejeita `ClOrdID`/`Symbol`/`SecurityID` vazios e usa domínios ilustrativos para
  `Side`/`OrdType` e `OrderQty > 0`; esses domínios não são regras universais inferíveis dos
  tipos FIX. O reader permissivo continua distinguindo um campo vazio recebido de ausência,
  sem declarar esse valor válido. O emitter obtém cada regra do
  tipo/campo/dicionário ou de política explícita; não deve universalizar "required"
  como "não vazio" nem "NUMINGROUP requerido" como contagem estritamente positiva.
- **Implementado (grupo opcional):** chamar `Skip{Group}()` em vez de
  `Begin{Group}(expectedCount)` ⇒ o tag `NUMINGROUP` nunca aparece no wire (ausência). Chamar
  `Begin{Group}(0)` e fechar sem nenhuma entrada ⇒ `NUMINGROUP=0`
  aparece explicitamente (zero explícito), **somente se a regra aplicável aceitar zero**; caso
  contrário, o fechamento falha. O spike usa um grupo ilustrativo que aceita zero.
  As duas coisas produzem `Count == 0` do lado do reader
  pela API de conveniência, mas são bytes diferentes no wire e diferenciáveis por uma segunda
  API de leitura ciente de presença — ver `ProtoPartyGroupReader.TryGetCount(out int)` no spike,
  testado em `OmittedGroup_IsAbsent_NotZero_OnTheWire` e
  `ExplicitEmptyGroup_WritesZero_DistinctFromOmission`. **Decisão proposta:** o reader gerado
  deveria expor as duas formas (`Count` de conveniência + `TryGetCount` ciente de presença), não
  só `Count`, já que a distinção é observável e alguns consumidores (auditoria/conformidade)
  podem precisar dela.

### 12.3 Obrigatoriedade contextual: componente opcional com filhos obrigatórios; grupo obrigatório não totalmente suprível na construção

- **Componente opcional com filho(s) obrigatório(s):** quando todos os campos obrigatórios do
  componente são conhecidos estaticamente (não são, eles mesmos, um sub-escopo com cardinalidade
  variável), a chamada `Begin{Component}(...)` colapsa naturalmente para um único parâmetro-list
  — ex. um componente opcional `Stipulations` cujo único filho obrigatório é `StipulationType`
  vira `BeginStipulations(stipulationType)`; nunca chamar essa API é a omissão do componente
  inteiro, chamá-la já garante seu filho obrigatório. Não há caso especial de codegen além do já
  descrito em §12.1.
- **Grupo obrigatório cuja regra aplicável exige ≥1 entrada:** C# consegue representar este
  typestate: `EmptyGroupWriter.AddEntry(...)` retorna `NonEmptyGroupWriter`, e só o segundo tipo
  expõe `EndGroup()`. Isso acrescenta tipos/transições e ainda não resolve cópias obsoletas.
  Alternativamente, um único tipo valida `_count >= 1` em runtime. **Recomendação:** usar a
  validação em runtime no primeiro emitter escopado, com erro explícito, e reservar o typestate
  vazio/não-vazio para dicionários que realmente declarem cardinalidade mínima. `required="Y"`
  por si só exige presença do campo contador; não se presume aqui que sempre implica valor > 0.

### 12.4 Ciclo de vida do handle do writer: fechamento, acesso ao pai durante filho ativo, fechamento repetido, handle obsoleto/copiado, propagação de falha

Typestate sozinho não torna um `ref struct` linear: uma cópia anterior conserva posição e estado
por valor. Também não é correto usar `Utf8JsonWriter` como precedente: ele é uma classe. Portanto,
**corrupção silenciosa por handle copiado é bloqueadora para um writer escopado de produção**.

O spike `ProtoSafeWriterOwner` demonstra uma alternativa limitada e segura sem mudar o wire:
o chamador fornece `Span<int>` de três posições (status, epoch, posição). Todos os handles
guardam esse mesmo span e seu epoch esperado. Cada transição incrementa o epoch compartilhado
e consome o handle de origem, entregando o novo epoch apenas ao handle retornado;
uma cópia obsoleta, fechamento repetido, acesso ao pai durante filho ativo e handle `default`
lançam `InvalidOperationException`. Falha de capacidade ou validação marca status `failed`,
incrementa o epoch e envenena todas as cópias. Não há rollback: os bytes parciais devem ser
descartados. Uso de handle obsoleto é rejeitado antes de alterar bytes ou invalidar o handle
atual. `SharedState_ConsumesEachSourceHandleOnTransition`,
`SharedState_RejectsStaleCopiedAndDefaultHandles` e
`SharedState_PoisonsAllCopiesAfterCapacityFailure` cobrem os caminhos representativos.

**Precondição de ownership:** a região de metadata é exclusiva de um owner. O chamador não
pode alterá-la, limpá-la ou passá-la a outro construtor enquanto algum handle anterior ainda
puder ser usado. O construtor inicializa o epoch; reutilizar a região prematuramente pode
reativar handles antigos e está fora das garantias do spike. A metadata também não pode se
sobrepor ao destino; o construtor rejeita essa sobreposição. O compilador restringe lifetime,
mas não garante exclusividade de spans. A API final deve documentar e minimizar esse risco,
preferindo metadata tipada a inteiros públicos.

```csharp
Span<int> state = stackalloc int[ProtoSafeWriterOwner.RequiredStateLength];
var owner = new ProtoSafeWriterOwner(destination, state);
var order = owner.BeginOrder(clOrdId);
```

Alternativas honestas:

| Forma | Segurança/lifetime | Trade-off |
|---|---|---|
| `Span<State>`/`Span<int>` fornecido pelo chamador (**recomendação do spike**) | Handles stack-only; storage pode ser stack ou heap e precisa permanecer válido e exclusivo. Compila com C# 11/net6. | Novo parâmetro/owner; o spike usa três inteiros, mas a implementação completa pode precisar de mais metadata por escopo. |
| Owner alocado (`class`) | Cópias compartilham naturalmente status/posição. | Alocação e lifetime de heap; abandona a meta allocation-free do caminho principal. |
| Typestate por valor sem owner | Bloqueia ordem nominal, mas não cópias divergentes. | **Não aceitável** como contrato seguro de produção. |
| Tipos lineares/uniqueness futuros | Poderiam impedir cópia em compilação. | Não existem em C# atual; não são base para esta API. |

O protótipo antigo por valor permanece no arquivo apenas para comparar wire order/backpatch; ele
**não** prova poison-on-failure nem ownership seguro. O emitter integrado usa
`Span<FixWriterState>` tipado em todas as fases, inclusive setters e grupos aninhados. Setters
renovam o epoch e invalidam cópias anteriores; transições entregam o novo epoch somente ao
handle retornado. Os tipos `Proto*` não são templates para essa implementação.

**Setters in-place:** caudas exclusivamente opcionais também expõem `void Set{Campo}(...)`.
O handle atual recebe o novo epoch e continua válido; cópias anteriores tornam-se obsoletas.
Não há um novo handle retornado nem mudança de fase. `Write{Campo}` reutiliza a mesma validação
e escrita, mas continua consumindo a origem e devolvendo um handle novo, assim como `Skip` e as
transições de escopo. Ambas as formas preservam ordem, poison, contagem e cópia imediata de spans;
misturá-las é permitido respeitando quais operações consomem a origem. Campos obrigatórios e
fases anteriores a escopos obrigatórios não ganham `Set`, portanto os construtores/factories
continuam obrigatórios. Nenhuma mudança de linguagem/runtime é necessária.

### 12.5 Ordem no wire quando campos obrigatórios e opcionais se intercalam; lifetime de spans de entrada

- **Ordem no wire:** as fases restringem as transições estruturais; a próxima rajada
  de campos obrigatórios já vem embutida no `End{Scope}(...)` anterior. Isso não prova toda a
  ordenação do dicionário em compile-time. Para evitar uma fase recursivamente duplicada por
  campo em dicionários grandes, sufixos compostos somente por entradas opcionais compartilham um
  único tipo de fase e mantêm um ordinal bounded no próprio handle. A checagem runtime rejeita
  duplicatas, escrita fora da ordem e reabertura de componente/grupo singular. Fronteiras com
  obrigatórios, ownership de filho, cardinalidade e poison continuam explícitos.
- **Delimitador estrutural de entrada:** toda entrada iniciada deve escrever primeiro o tag
  delimitador resolvido do grupo, mesmo quando o `<field>` correspondente tem `required='N'`.
  `required` controla presença de schema fora desse papel estrutural; não autoriza uma entrada
  vazia nem uma entrada iniciada por outro membro. A validação runtime também cobre delimitadores
  localizados dentro de um componente líder e counters de grupo aninhado.
- **Spans de entrada (scratch):** mantém a decisão já tomada e implementada na issue #26 (§2.2) —
  toda escrita de span (`Wire.WriteSpan` no spike, `FixSpanWriter.WriteField`/`BeginMessage` na
  implementação real) copia os bytes imediatamente; nenhuma referência ao span de entrada é
  retida além da chamada. Isso é reprovado explicitamente pelo teste
  `StackallocInput_CanBeReusedImmediatelyAfterWrite`: o mesmo buffer `stackalloc` é reutilizado
  para dois campos diferentes de duas mensagens/escopos diferentes, e o primeiro valor
  permanece correto no destino. **Nenhuma mudança de linguagem/runtime é necessária aqui** — o
  padrão já funciona em net6+/C# 11+ (mesmo TFM mínimo já documentado em §2.2, sem aumento).

### 12.6 Contagem de grupo: quantidade esperada antecipada vs. backpatch automático

Prototipado em `ProtoPartyGroupWriter` (backpatch) e `ProtoPartyGroupWriterCounted` (antecipada),
comparados diretamente em `BackpatchGroup_ShiftsBytesWhenDigitWidthGrows` e
`UpfrontCountVariant_*`:

| | **Backpatch** (`Begin{Group}()`, sem contagem) | **Contagem antecipada** (`Begin{Group}(int expectedCount)`) |
|---|---|---|
| Quando o `NUMINGROUP` é conhecido pelo chamador | Não precisa ser conhecido antes de escrever entradas. | Precisa ser conhecido *antes* da primeira entrada. |
| Custo de `EndGroup()`/escrita do contador | **Não é O(1).** Reserva-se uma largura de placeholder (o spike usa 1 dígito, análogo ao placeholder de 6 dígitos que `FixSpanWriter.Finish()` já reserva para `BodyLength`); se a contagem real precisar de mais dígitos, todos os bytes escritos desde o placeholder até a posição atual precisam ser deslocados (`memmove`) antes dos dígitos finais serem gravados — `BackpatchGroup_ShiftsBytesWhenDigitWidthGrows` força esse caminho com 11 entradas contra um placeholder de 1 dígito. | **O(1)** — os dígitos finais são gravados uma única vez, na posição definitiva, sem deslocamento. |
| Detecção de erro | O contador final corresponde às entradas concluídas; capacidade insuficiente, valores inválidos e violação de cardinalidade explícita ainda podem falhar. `required` sozinho não estabelece mínimo positivo (§12.3). | Também sujeito a capacidade/valores/cardinalidade; falha na (N+1)-ésima `AddEntry` (excesso) ou em `EndGroup()` (falta). O contador já pode estar gravado; descarte o destino inteiro, sem rollback (§2.2/12.4). |
| Ergonomia para o chamador | Chamador não precisa pré-calcular a contagem (útil quando as entradas vêm de uma fonte que só sabe seu tamanho ao terminar de iterar). | Chamador precisa saber a contagem adiantada (útil quando a fonte já é um array/lista com `.Length`/`.Count` conhecido — motivo mais comum de já se ter a contagem à mão). |

**Decisão implementada em #31:** oferecer somente a variante de **contagem antecipada** como
primeira implementação completa. Não se presume que backpatch seja gratuito: a variante sem
contagem permanece candidata separada e só deve ser adicionada depois de medir largura de
placeholder, deslocamento e impacto de superfície nos dicionários reais (§12.11).

### 12.7 Reader seletivo, presença, acesso repetido/conversão, duplicatas, tags desconhecidas, fronteiras de grupo aninhado, lifetime da view

Prototipado em `ProtoOrderReader`/`ProtoOrderView`/`ProtoPartyGroupReader.Enumerator`
(`SelectiveProjection_View_*`, `UnknownTag_IsSkipped_WithoutDisturbingKnownFields`):

- **Reutiliza o modelo de projeção do `[FixView]` (§11):** `ProtoOrderView`
  localiza os N campos pedidos (aqui, 2: `ClOrdID`+`Price`) e para assim que todos foram vistos;
  se um campo opcional pedido nunca aparecer, o scan simplesmente chega ao fim do buffer (mesma
  limitação já documentada em §11 — não há como saber antecipadamente "não vai aparecer mais").
  Nenhuma mudança de shape é proposta aqui além do que #13/#17 já definiram; a única novidade é
  usar esse mesmo modelo também para as views/leituras *dentro* de escopos aninhados (não só na
  mensagem inteira).
- **Presença/campo repetido/conversão:** mesma convenção de §4/§2.1 (flag `_present` +
  getter/`TryGet`) — sem mudança proposta. Ler o mesmo campo várias vezes (`reader.ClOrdID`
  chamado duas vezes) sempre retorna o mesmo resultado (o scan já rodou uma vez no construtor;
  os getters só fatiam o mesmo `(start, length)` gravado) — barato e determinístico, sem
  reconversão de estado.
- **Tags duplicadas:** **Atual:** o reader completo continua varrendo e sobrescrevendo offsets,
  portanto observa a última ocorrência. **Proposto para projeções com early-exit:** primeira
  ocorrência por escopo vence; depois que todos os campos pedidos foram encontrados, bytes
  posteriores não são observados. `ProjectionUsesFirstOccurrenceAcrossEarlyExitBoundary`
  demonstra duplicata antes e depois da fronteira e também explicita a diferença atual para o
  reader completo. Não se atribui esse comportamento à especificação FIX nem a retransmissão.
  Consumidores que exigem rejeição de duplicatas precisam de um modo estrito/full-scan separado;
  early-exit e validação incondicional de duplicatas são semanticamente incompatíveis.
- **Tags desconhecidas:** já ignoradas pelo `switch`/`default` implícito hoje (§8, FIX003/FIX006
  cobrem isso no parse do schema, não no reader) — `UnknownTag_IsSkipped_WithoutDisturbingKnownFields`
  prova isso com bytes manuscritos contendo um tag 999 não mapeado entre dois campos conhecidos.
  Nenhuma mudança proposta.
- **Fronteiras de grupo aninhado:** delimitador sozinho é insuficiente. O enumerador usa
  conjuntamente (1) `NUMINGROUP` declarado, (2) delimitador da entrada e (3) conjunto achatado de
  tags membro, incluindo counter/delimitador/membros de grupos aninhados — o mesmo mecanismo do
  `FixGroupEnumerator` real. Para em count zero, após a quantidade declarada, diante de
  delimitador ausente ou primeira tag não membro do pai. Count maior que bytes disponíveis
  termina sem fabricar entrada; count menor não absorve a entrada extra. O spike cobre os quatro
  casos, boundary por tag desconhecida/não membro e um grupo realmente aninhado.
- **Lifetime da view/entry através da enumeração:** `ref struct` impede heap, boxing, captura por
  lambda e travessia de `await`; CS8175 prova apenas a proibição de captura. Ele **não** expira em
  `MoveNext()`, pode conter outros campos `ref struct`, e uma cópia local de
  `{Group}EntryReader` continua válida enquanto o span subjacente for válido.
  `EntryCopyRemainsValidAfterEnumeratorAdvances` demonstra esse caso positivo. Se uma futura
  implementação reutilizar storage mutável de lookup em vez de offsets imutáveis por valor,
  deverá armazenar epoch compartilhado e validar cada getter; esta proposta não reutiliza tal
  storage e portanto não cria invalidação por iteração.

### 12.8 Invariantes por construção vs. validação em runtime (resumo)

| Invariante | Mecanismo |
|---|---|
| Campo obrigatório de mensagem/componente/entrada presente antes de prosseguir | **Construção** (parâmetro obrigatório do construtor/`Begin{Scope}`/`End{Scope}` — §12.1). |
| Ordem de escrita respeitando o wire | **Construção + runtime**: fases delimitam obrigatórios/escopos; ordinal bounded rejeita opcionais duplicados ou fora de ordem (§12.1/§12.5). |
| Omissão distinta de zero/false/span vazio | Chamar ou não chamar o método determina presença; valores inválidos são rejeitados, não omitidos (§12.2). Reader usa flag `_present`. |
| Componente opcional com filho(s) obrigatório(s) conhecido(s) estaticamente | **Construção** (parâmetro de `Begin{Component}` — §12.3). |
| Grupo com cardinalidade mínima ≥1, quando estabelecida pelo dicionário/regra | **Runtime recomendado** inicialmente; typestate `Empty` → `NonEmpty` é alternativa possível (§12.3). |
| Acesso ao pai durante filho ativo / fechamento repetido / handle copiado ou default | **Runtime compartilhado** (owner + epoch/status fora do wire, §12.4). |
| Contagem de grupo (upfront) bate com entradas reais | **Runtime** (`EndGroup()`/`AddEntry` da variante contada, §12.6) — dígitos errados podem já estar gravados antes do erro ser detectado. |
| Lifetime de view/entry | **Compilador** impede escape para heap/`await`; cópia local permanece válida enquanto o span de origem for válido e não expira em `MoveNext()` (§12.7). |
| Lifetime de spans de entrada (`stackalloc` reutilizável após a chamada) | **Construção** (cópia imediata, sem retenção — §12.5, já implementado desde a issue #26). |
| Tags duplicadas / desconhecidas no reader | **Runtime:** projeção early-exit usa primeira ocorrência; reader completo atual usa última; desconhecidas são ignoradas. Validação estrita requer full scan (§12.7). |

### 12.9 Dono único de integração para mudanças de runtime compartilhado

Antes de dois agentes independentes implementarem reader e writer em paralelo, **uma única PR/
issue de integração** deve:
1. Decidir a forma final dos tipos de fase (typestate) por mensagem/componente/grupo — nomes,
   convenção de sufixo (o spike usa `{X}Writer`/`{X}WriterHandle`, mas a convenção real cabe ao
   dono de integração escolher e documentar em §5).
2. Decidir a largura do placeholder de `NUMINGROUP` para a variante backpatch (§12.6) com base
   em medição real (§12.11), não em um número arbitrário.
3. Decidir se a variante de contagem antecipada é obrigatória, opcional, ou a única oferecida —
   ambas funcionam (§12.6 prova as duas), mas manter as duas dobra a superfície de código gerado
   por grupo.
4. Integrar **uma única implementação de owner compartilhado** (status/epoch/posição e poison)
   em `RuntimeGenerator.cs`, incluindo handles default/copiados, antes de qualquer emitter
   escopado ser considerado seguro. Reader e writer não devem criar versões divergentes desse
   estado compartilhado.
5. Só depois disso, `WriterEmitter.cs` e `ReaderEmitter.cs` podem ser reescritos por agentes
   separados sem risco de convergirem em shapes incompatíveis (esta é exatamente a
   responsabilidade que o próprio issue #29 lista como não pertencendo ao seu escopo).

### 12.10 Exemplos de migração (achatado → escopado)

```csharp
// Atual (WriterEmitter.cs, achatado, §2.2/§6):
var w = new NewOrderSingleWriter(destination);
w.WriteClOrdID("ORD-1"u8);
w.WriteSymbol("EUR/USD"u8);          // Instrument.Symbol, achatado direto no writer da mensagem
w.WriteSide('1');
w.WriteOrderQty(100_000m);
w.WriteOrdType('1');
w.WritePrice(1.2345m);
w.WriteNoPartyIDs(2);                 // contador manual — chamador calcula e nunca é validado
// ... WriteField(int tag, ...) dinâmico para cada entrada do grupo, sem sub-escopo dedicado ...
int length = w.Finish();
```

```csharp
// Proposto (§12.1-§12.6, ilustrativo — nomes exatos ficam para o dono de integração, §12.9):
Span<int> state = stackalloc int[ScopedWriterOwner.RequiredStateLength];
var owner = new ScopedWriterOwner(destination, state);
var writer = owner.BeginNewOrderSingle("ORD-1"u8);
var instrument = writer.BeginInstrument("EUR/USD"u8);
var tail = instrument.EndInstrument(side: '1', orderQty: 100_000m, ordType: '1');
tail.WritePrice(1.2345m);
tail = tail.BeginOptionalBroker("BROKER"u8) // componente omitido se Begin não for chamado;
           .EndOptionalBroker();            // se presente, Broker é obrigatório/não vazio
var group = tail.BeginNoPartyIDs();   // ou tail.BeginNoPartyIDs(expectedCount: 2), ver §12.6
group.AddEntry("PARTY-A"u8, partyIdSource: 'D', partyRole: 3);
var entry = group.BeginEntry("PARTY-B"u8);
var nested = entry.BeginNoNestedPartyIDs();
nested.AddEntry("SUB-1"u8, role: 1);
entry = nested.EndGroup();
group = entry.EndEntry();
int length = group.EndGroup().Finish();
```

O exemplo combinado acima **não é executável ainda**. Os spikes separados estão em
`tests/FixSourceGenerator.Tests/ScopedApiContractExamples.cs` +
`ScopedApiContractExamplesTests.cs` (`dotnet test --filter
FullyQualifiedName~ScopedApiContractExamplesTests`). O protótipo por valor demonstra
wire order/contadores/round-trip de corpos; `ProtoSafeWriterOwner` demonstra ownership num
subconjunto sem setters opcionais nem grupos. O grupo aninhado do spike é uma operação
limitada, não a cadeia de handles `BeginEntry`/`EndEntry` ilustrada acima.

### 12.11 Cenários de benchmark para decidir entre as formas viáveis

Nenhum destes foi executado como parte deste issue (medição de performance é responsabilidade do
integrador, não deste design) — ficam registrados aqui para orientar quem for medir:

1. **Achatado (atual) vs. escopado (proposto), mesma mensagem:** tempo de encode + bytes de
   código gerado (IL size) para uma mensagem com 1 componente obrigatório + 1 grupo opcional de
   N entradas, N ∈ {0, 1, 10, 100}. Medir separadamente owner com metadata stack-only e owner
   alocado; não presumir custo, inlining, tamanho de código ou ausência de alocação.
2. **Backpatch vs. contagem antecipada (§12.6), grupos grandes:** medir o custo do `memmove` de
   `EndGroup()` quando a largura do placeholder é excedida, variando N e a largura reservada
   (2 vs. 3 dígitos) contra os dicionários reais já usados pelos benchmarks existentes
   (`FIX44-quickfixn-dictionary.xml`, variando a quantidade real de entradas, não confundindo-a
   com a quantidade de tags possíveis do schema) — usar `benchmarks/FixSourceGenerator.Benchmarks` como harness, mesmo padrão dos
   experimentos existentes (`benchmarks/experiments/README.md`).
3. **`[FixView]`-estilo seletivo vs. reader completo, para leitura dentro de escopos aninhados
   (§12.7):** já parcialmente coberto pelas benchmarks de grupo existentes
   (`FixViewBenchmarks.cs`/`MarketDataReaderBenchmarks`) — estender para um cenário de view
   seletiva dentro de uma entrada de grupo profundamente aninhada, comparando com o reader
   completo da mesma entrada.
4. **Owner compartilhado (§12.4):** comparar metadata caller-owned (`Span<int>`/`Span<State>`)
   com owner alocado e writer achatado, incluindo caminho feliz e falha envenenada. Só considerar
   hints de inlining depois da medição.

### 12.12 Decisão e trade-offs explícitos

**Recomendação:** adotar o shape escopado/typestate com **owner compartilhado caller-owned**
(§12.1-§12.7) como direção para a próxima
geração de writer, mantendo o reader já descrito em §2.1/§11 (eager-locate/lazy-parse + `[FixView]`)
praticamente inalterado — a mudança de reader proposta aqui é só estender o mesmo modelo para
escopos aninhados (§12.7), não redesenhá-lo. Trade-offs assumidos conscientemente:

- **Mais tipos gerados por mensagem** (um `ref struct` por fase de escopo obrigatório) em troca de
  invariantes de ordem/obrigatoriedade checadas em tempo de compilação em vez de só em runtime —
  aceito, é o mesmo espírito de "erro em tempo de compilação em vez de erro em produção" que já
  motiva o resto do gerador (§0).
- **Handle obsoleto/copiado deve falhar**, não corromper silenciosamente (§12.4). A integração do
  owner compartilhado e poison é condição de entrada para emitters de produção; typestate apenas
  por valor não é aprovado.
- **Nenhum aumento de TFM foi demonstrado como necessário.** O mesmo fonte do spike é compilado
  pelo projeto de compatibilidade real `net6.0`/C# 11 via `Compile Link`; os testes comportamentais
  rodam em `net9.0`. Isso prova o floor de compilação, não uma execução em runtime net6. O requisito
  mais novo de `[FixView]` vem de **partial properties** (C# 13), não de `partial ref struct`.
- **Duas políticas de contagem de grupo (§12.6) coexistindo** aumenta a superfície de API por
  grupo — aceito como proposta inicial; o dono de integração (§12.9) pode decidir reduzir para
  uma só, com base nos benchmarks de §12.11.
