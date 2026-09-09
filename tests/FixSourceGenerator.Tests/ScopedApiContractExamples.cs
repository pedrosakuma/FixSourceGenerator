using System;
using System.Buffers.Text;

namespace FixSourceGenerator.Tests.ScopedApiContractExamples
{
    /// <summary>
    /// Feasibility spike for issue #29 (docs/CONTRACT.md §12). These types are hand-written, not
    /// generated, and deliberately skip the envelope machinery (BeginString/BodyLength/CheckSum)
    /// that already exists and works in <c>FixSpanWriter</c>/<c>FixSpanReader</c> (RuntimeGenerator.cs) —
    /// this spike is only about proving the *scoping/typestate* shape (required-input placement,
    /// component/group scope closure, parent access while a child scope is active, group-count
    /// policy, span lifetime, selective projection), not about re-deriving already-solved framing.
    /// None of this is wired into the real generator (WriterEmitter.cs/ReaderEmitter.cs); it exists
    /// purely to prove the proposed shape compiles and behaves as specified before an implementer
    /// commits to it. Modeled loosely on the NewOrderSingle/Instrument/NoPartyIDs shapes already
    /// used by benchmarks/FixSourceGenerator.Benchmarks/Schema/FIX44-mini.xml.
    /// </summary>
    internal static class Wire
    {
        public const byte Soh = 0x01;

        public static void WriteTag(Span<byte> buf, ref int pos, int tag)
        {
            Span<byte> formatted = stackalloc byte[11];
            if (!Utf8Formatter.TryFormat(tag, formatted, out int written))
            {
                throw new ArgumentException("Destination too small.", nameof(buf));
            }

            EnsureCapacity(buf, pos, written + 1);
            formatted.Slice(0, written).CopyTo(buf.Slice(pos));
            pos += written;
            buf[pos++] = (byte)'=';
        }

        public static void WriteSpan(Span<byte> buf, ref int pos, int tag, ReadOnlySpan<byte> value)
        {
            // Contract (issue #26, already shipped): the input span is copied immediately —
            // stackalloc'd callers can reuse/overwrite their buffer right after this call returns.
            WriteTag(buf, ref pos, tag);
            EnsureCapacity(buf, pos, value.Length + 1);
            value.CopyTo(buf.Slice(pos));
            pos += value.Length;
            buf[pos++] = Soh;
        }

        public static void WriteChar(Span<byte> buf, ref int pos, int tag, char value)
        {
            WriteTag(buf, ref pos, tag);
            EnsureCapacity(buf, pos, 2);
            buf[pos++] = (byte)value;
            buf[pos++] = Soh;
        }

        public static void WriteInt(Span<byte> buf, ref int pos, int tag, int value)
        {
            Span<byte> formatted = stackalloc byte[11];
            if (!Utf8Formatter.TryFormat(value, formatted, out int written))
            {
                throw new ArgumentException("Destination too small.", nameof(buf));
            }

            WriteSpan(buf, ref pos, tag, formatted.Slice(0, written));
        }

        public static void WriteDecimal(Span<byte> buf, ref int pos, int tag, decimal value)
        {
            Span<byte> formatted = stackalloc byte[31];
            if (!Utf8Formatter.TryFormat(value, formatted, out int written))
            {
                throw new ArgumentException("Destination too small.", nameof(buf));
            }

            WriteSpan(buf, ref pos, tag, formatted.Slice(0, written));
        }

        private static void EnsureCapacity(Span<byte> buffer, int position, int required)
        {
            if ((uint)position > (uint)buffer.Length || required > buffer.Length - position)
            {
                throw new ArgumentException("Destination too small.", nameof(buffer));
            }
        }

        /// <summary>Scans one <c>tag=value&lt;SOH&gt;</c> field starting at <paramref name="pos"/>.</summary>
        public static bool TryReadField(ReadOnlySpan<byte> buf, int pos, out int tag, out int valueStart, out int valueLength, out int nextPos)
        {
            tag = 0;
            valueStart = 0;
            valueLength = 0;
            nextPos = pos;
            if (pos >= buf.Length)
            {
                return false;
            }

            int eq = buf.Slice(pos).IndexOf((byte)'=');
            if (eq < 0)
            {
                return false;
            }

            if (!Utf8Parser.TryParse(buf.Slice(pos, eq), out int parsedTag, out _))
            {
                return false;
            }

            int valueStartAbs = pos + eq + 1;
            int soh = buf.Slice(valueStartAbs).IndexOf(Soh);
            if (soh < 0)
            {
                return false;
            }

            tag = parsedTag;
            valueStart = valueStartAbs;
            valueLength = soh;
            nextPos = valueStartAbs + soh + 1;
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Writer side: a typestate machine. Each "Begin*"/"End*" call returns a *different*
    // struct type exposing only the members valid for that phase, so the compiler statically
    // forbids most misuse (e.g. writing an optional body field before the required Instrument
    // component has been closed). What it can NOT statically forbid — because C# structs are
    // freely copyable — is a caller keeping a stale copy. The first prototype below intentionally
    // demonstrates only typestate/wire shape. ProtoSafeWriterOwner later in this file adds shared
    // caller-owned epoch/status metadata and is the ownership-safe candidate (docs/CONTRACT.md §12.4).
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Phase 0. Required message-scope input (ClOrdID) is a constructor parameter — not a
    /// transitive parameter of some giant all-fields constructor. The next required scope
    /// (Instrument, a required component) is reached only through <see cref="BeginInstrument"/>.
    /// </summary>
    public ref struct ProtoOrderWriter
    {
        private Span<byte> _buf;
        private int _pos;
        private sbyte _state; // 0 = awaiting BeginInstrument, 1 = consumed

        public ProtoOrderWriter(Span<byte> destination, scoped ReadOnlySpan<byte> clOrdId)
        {
            if (clOrdId.IsEmpty)
            {
                throw new ArgumentException("Required ClOrdID must not be empty.", nameof(clOrdId));
            }

            _buf = destination;
            _pos = 0;
            _state = 0;
            Wire.WriteSpan(_buf, ref _pos, 11, clOrdId); // ClOrdID
        }

        /// <summary>Enters the required Instrument component scope. Symbol is Instrument's own required child.</summary>
        public ProtoInstrumentWriter BeginInstrument(scoped ReadOnlySpan<byte> symbol)
        {
            if (_state != 0)
            {
                throw new InvalidOperationException("BeginInstrument was already called on this writer handle.");
            }

            if (symbol.IsEmpty)
            {
                throw new ArgumentException("Required Symbol must not be empty.", nameof(symbol));
            }

            _state = 1;
            Wire.WriteSpan(_buf, ref _pos, 55, symbol); // Instrument.Symbol (required)
            return new ProtoInstrumentWriter(_buf, _pos);
        }
    }

    /// <summary>
    /// Phase 1 (Instrument component open). <see cref="WriteSecurityID"/> is optional and may be
    /// omitted entirely — omission is distinct from writing an explicit empty span (see the
    /// contract-level distinction table in docs/CONTRACT.md §12.2).
    /// </summary>
    public ref struct ProtoInstrumentWriter
    {
        private Span<byte> _buf;
        private int _pos;
        private sbyte _state; // 0 = open, 1 = closed

        internal ProtoInstrumentWriter(Span<byte> buf, int pos)
        {
            _buf = buf;
            _pos = pos;
            _state = 0;
        }

        public void WriteSecurityID(scoped ReadOnlySpan<byte> value)
        {
            if (_state != 0)
            {
                throw new InvalidOperationException("Instrument scope already closed.");
            }

            if (value.IsEmpty)
            {
                throw new ArgumentException("An explicit SecurityID must not be empty.", nameof(value));
            }

            Wire.WriteSpan(_buf, ref _pos, 48, value); // Instrument.SecurityID (optional)
        }

        /// <summary>
        /// Closes the Instrument scope and, in the same call, supplies the message's next
        /// required run of scalar fields (Side/OrderQty/OrdType) — resolving wire-order
        /// interleaving between a required sub-scope and the required fields that follow it
        /// (docs/CONTRACT.md §12.3) without exposing them as three independently-orderable calls.
        /// </summary>
        public ProtoOrderTailWriter EndInstrument(char side, decimal orderQty, char ordType)
        {
            if (_state != 0)
            {
                throw new InvalidOperationException("Instrument scope already closed.");
            }

            if (side != '1' && side != '2')
            {
                throw new ArgumentOutOfRangeException(nameof(side));
            }

            if (orderQty <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(orderQty));
            }

            if (ordType != '1' && ordType != '2')
            {
                throw new ArgumentOutOfRangeException(nameof(ordType));
            }

            _state = 1;
            Wire.WriteChar(_buf, ref _pos, 54, side);
            Wire.WriteDecimal(_buf, ref _pos, 38, orderQty);
            Wire.WriteChar(_buf, ref _pos, 40, ordType);
            return new ProtoOrderTailWriter(_buf, _pos);
        }
    }

    /// <summary>
    /// Phase 2 (message body, after the only required component). Optional Price and an optional
    /// repeated group (NoPartyIDs) both live here, plus <see cref="Finish"/> — a required group's
    /// "at least one entry" invariant, in contrast, can NOT be enforced by this construction: see
    /// <see cref="ProtoPartyGroupWriter.EndGroup"/>/<see cref="ProtoPartyGroupWriterCounted.EndGroup"/>.
    /// </summary>
    public ref struct ProtoOrderTailWriter
    {
        private Span<byte> _buf;
        private int _pos;
        private sbyte _state; // 0 = open, 1 = group scope active (parent access blocked), 2 = finished

        internal ProtoOrderTailWriter(Span<byte> buf, int pos)
        {
            _buf = buf;
            _pos = pos;
            _state = 0;
        }

        public void WritePrice(decimal value)
        {
            if (_state != 0)
            {
                throw new InvalidOperationException("Tail scope is not open (group active or already finished).");
            }

            Wire.WriteDecimal(_buf, ref _pos, 44, value); // Price (optional)
        }

        /// <summary>
        /// Backpatch-count variant (docs/CONTRACT.md §12.5, policy A): entries are appended
        /// without knowing the count upfront; the counter is fixed up when the group closes.
        /// Omitting this call entirely means tag 453 never appears on the wire (absence), which
        /// is different from calling it and closing with zero entries (explicit zero, tag 453=0).
        /// </summary>
        public ProtoPartyGroupWriter BeginNoPartyIDs()
        {
            if (_state != 0)
            {
                throw new InvalidOperationException("A group scope is already active, or the writer is finished.");
            }

            _state = 1;
            return new ProtoPartyGroupWriter(_buf, _pos);
        }

        /// <summary>
        /// Upfront-count variant (docs/CONTRACT.md §12.5, policy B): the caller states the entry
        /// count before writing any entry. No placeholder/backpatch/memmove is needed because the
        /// digits are final the moment they're written — but a wrong count is only caught at
        /// <see cref="ProtoPartyGroupWriterCounted.EndGroup"/> (under-count) or immediately on the
        /// (N+1)th <see cref="ProtoPartyGroupWriterCounted.AddEntry"/> (over-count), and by then
        /// the wrong digits are already committed to the destination — the whole destination must
        /// be discarded on mismatch, same "no rollback of partially written fields" contract
        /// already documented for capacity failures (docs/CONTRACT.md §2.2).
        /// </summary>
        public ProtoPartyGroupWriterCounted BeginNoPartyIDs(int expectedCount)
        {
            if (_state != 0)
            {
                throw new InvalidOperationException("A group scope is already active, or the writer is finished.");
            }

            if (expectedCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedCount));
            }

            _state = 1;
            Wire.WriteInt(_buf, ref _pos, 453, expectedCount);
            return new ProtoPartyGroupWriterCounted(_buf, _pos, expectedCount);
        }

        public int Finish()
        {
            if (_state == 1)
            {
                throw new InvalidOperationException("A group scope is still active; call EndGroup() first.");
            }

            if (_state == 2)
            {
                throw new InvalidOperationException("The writer is already finished.");
            }

            _state = 2;
            return _pos;
        }

        internal void ResumeAfterGroup(int pos)
        {
            _pos = pos;
            _state = 0;
        }
    }

    /// <summary>Group-entry writer. PartyID is a required per-entry field, so it is a plain parameter of <see cref="AddEntry"/>, not a separate optional call.</summary>
    public ref struct ProtoPartyGroupWriter
    {
        private const int CountPlaceholderWidth = 1;

        private Span<byte> _buf;
        private int _pos;
        private readonly int _countValuePos;
        private int _count;
        private bool _ended;

        internal ProtoPartyGroupWriter(Span<byte> buf, int pos)
        {
            _buf = buf;
            _pos = pos;
            _count = 0;
            _ended = false;

            // Reserve a single-digit placeholder for tag 453 (NUMINGROUP); EndGroup() below
            // back-patches it and, when the real count needs more digits, shifts every byte
            // written since (the whole group body) to make room — i.e. backpatching here is
            // O(group-body-bytes-so-far), not O(1). Same technique FixSpanWriter.Finish() already
            // uses for BodyLength (RuntimeGenerator.cs), reserving a wider placeholder there
            // specifically to make that shift rare in practice.
            Wire.WriteTag(_buf, ref _pos, 453);
            _countValuePos = _pos;
            _buf[_pos++] = (byte)'0';
            _buf[_pos++] = Wire.Soh;
        }

        public void AddEntry(scoped ReadOnlySpan<byte> partyId, char? partyIdSource, int? partyRole)
        {
            if (_ended)
            {
                throw new InvalidOperationException("Group already closed.");
            }

            Wire.WriteSpan(_buf, ref _pos, 448, partyId); // required
            if (partyIdSource is char src)
            {
                Wire.WriteChar(_buf, ref _pos, 447, src); // optional
            }

            if (partyRole is int role)
            {
                Wire.WriteInt(_buf, ref _pos, 452, role); // optional
            }

            _count++;
        }

        // Small fixed-arity feasibility example for a real nested repeating group. A generated
        // API would expose its own nested entry typestate rather than this two-entry shortcut.
        public void AddEntryWithTwoNestedParties(
            scoped ReadOnlySpan<byte> partyId,
            scoped ReadOnlySpan<byte> firstNestedPartyId,
            scoped ReadOnlySpan<byte> secondNestedPartyId)
        {
            if (_ended)
            {
                throw new InvalidOperationException("Group already closed.");
            }

            if (partyId.IsEmpty || firstNestedPartyId.IsEmpty || secondNestedPartyId.IsEmpty)
            {
                throw new ArgumentException("Required entry values must not be empty.");
            }

            Wire.WriteSpan(_buf, ref _pos, 448, partyId);
            Wire.WriteInt(_buf, ref _pos, 802, 2);
            Wire.WriteSpan(_buf, ref _pos, 523, firstNestedPartyId);
            Wire.WriteInt(_buf, ref _pos, 803, 1);
            Wire.WriteSpan(_buf, ref _pos, 523, secondNestedPartyId);
            Wire.WriteInt(_buf, ref _pos, 803, 2);
            _count++;
        }

        public ProtoOrderTailWriterHandle EndGroup()
        {
            if (_ended)
            {
                throw new InvalidOperationException("Group already closed.");
            }

            _ended = true;

            Span<byte> digits = stackalloc byte[10];
            if (!Utf8Formatter.TryFormat(_count, digits, out int digitCount))
            {
                throw new InvalidOperationException("Count could not be formatted.");
            }

            int delta = digitCount - CountPlaceholderWidth;
            if (delta != 0)
            {
                int regionStart = _countValuePos;
                int regionLength = _pos - regionStart;
                _buf.Slice(regionStart, regionLength).CopyTo(_buf.Slice(regionStart + delta));
                _pos += delta;
            }

            digits.Slice(0, digitCount).CopyTo(_buf.Slice(_countValuePos));
            return new ProtoOrderTailWriterHandle(_buf, _pos);
        }
    }

    /// <summary>Upfront-count group-entry writer (policy B, see <see cref="ProtoOrderTailWriter.BeginNoPartyIDs(int)"/>).</summary>
    public ref struct ProtoPartyGroupWriterCounted
    {
        private Span<byte> _buf;
        private int _pos;
        private readonly int _expectedCount;
        private int _added;
        private bool _ended;

        internal ProtoPartyGroupWriterCounted(Span<byte> buf, int pos, int expectedCount)
        {
            _buf = buf;
            _pos = pos;
            _expectedCount = expectedCount;
            _added = 0;
            _ended = false;
        }

        public void AddEntry(scoped ReadOnlySpan<byte> partyId, char? partyIdSource, int? partyRole)
        {
            if (_ended)
            {
                throw new InvalidOperationException("Group already closed.");
            }

            if (_added >= _expectedCount)
            {
                // The wrong tag 453 digits are already committed to the destination (see the
                // BeginNoPartyIDs(int) remarks) — the caller must discard this destination buffer.
                throw new InvalidOperationException($"AddEntry called more times than the declared expectedCount ({_expectedCount}).");
            }

            Wire.WriteSpan(_buf, ref _pos, 448, partyId);
            if (partyIdSource is char src)
            {
                Wire.WriteChar(_buf, ref _pos, 447, src);
            }

            if (partyRole is int role)
            {
                Wire.WriteInt(_buf, ref _pos, 452, role);
            }

            _added++;
        }

        public ProtoOrderTailWriterHandle EndGroup()
        {
            if (_ended)
            {
                throw new InvalidOperationException("Group already closed.");
            }

            if (_added != _expectedCount)
            {
                throw new InvalidOperationException($"Declared expectedCount was {_expectedCount} but {_added} entries were written; discard this destination.");
            }

            _ended = true;
            return new ProtoOrderTailWriterHandle(_buf, _pos);
        }
    }

    /// <summary>
    /// Returned by both group-writer variants' <c>EndGroup()</c>: resumes the parent tail scope
    /// (parent access, blocked while the child group was active per the enclosing
    /// <c>ProtoOrderTailWriter._state</c> guard, becomes available again). Kept as its own tiny
    /// type — rather than resurrecting the exact original <see cref="ProtoOrderTailWriter"/>
    /// instance — because a ref struct value handed back out of a nested scope is, deliberately,
    /// a *new* handle: it carries only what the next phase needs (buffer + position), not
    /// left-over state from the closed group.
    /// </summary>
    public ref struct ProtoOrderTailWriterHandle
    {
        private readonly Span<byte> _buf;
        private readonly int _pos;

        internal ProtoOrderTailWriterHandle(Span<byte> buf, int pos)
        {
            _buf = buf;
            _pos = pos;
        }

        public int Finish() => _pos;
    }

    // Copy-safe ownership feasibility spike. The caller supplies three integers of metadata;
    // every copied handle observes the same epoch/status. This metadata is out-of-band and does
    // not alter FIX bytes. It is intentionally bounded to the lifecycle guarantees under review,
    // rather than duplicating the complete production writer.

    public ref struct ProtoSafeWriterOwner
    {
        public const int RequiredStateLength = 3;
        private Span<byte> _destination;
        private Span<int> _state;

        public ProtoSafeWriterOwner(Span<byte> destination, Span<int> state)
        {
            if (state.Length < RequiredStateLength)
            {
                throw new ArgumentException("Three state integers are required.", nameof(state));
            }
            if (destination.Overlaps(System.Runtime.InteropServices.MemoryMarshal.AsBytes(state.Slice(0, RequiredStateLength))))
            {
                throw new ArgumentException("Writer metadata must not overlap the FIX destination.", nameof(state));
            }

            _destination = destination;
            _state = state.Slice(0, RequiredStateLength);
            _state.Clear();
            _state[1] = 1; // epoch; zero remains reserved for default handles
        }

        public ProtoSafeOrderWriter BeginOrder(scoped ReadOnlySpan<byte> clOrdId)
        {
            if (_state.IsEmpty || _state[0] != 0 || _state[1] != 1)
            {
                throw new InvalidOperationException("Owner is default, failed, or already used.");
            }

            if (clOrdId.IsEmpty)
            {
                Fail();
                throw new ArgumentException("Required ClOrdID must not be empty.", nameof(clOrdId));
            }

            WriteSpan(11, clOrdId);
            _state[1]++;
            return new ProtoSafeOrderWriter(_destination, _state, _state[1]);
        }

        private void WriteSpan(int tag, scoped ReadOnlySpan<byte> value)
        {
            try
            {
                int pos = _state[2];
                Wire.WriteSpan(_destination, ref pos, tag, value);
                _state[2] = pos;
            }
            catch (ArgumentException)
            {
                Fail();
                throw;
            }
        }

        private void Fail()
        {
            if (!_state.IsEmpty)
            {
                _state[0] = -1;
                _state[1]++;
            }
        }
    }

    public ref struct ProtoSafeOrderWriter
    {
        private Span<byte> _destination;
        private Span<int> _state;
        private int _epoch;

        internal ProtoSafeOrderWriter(Span<byte> destination, Span<int> state, int epoch)
        {
            _destination = destination;
            _state = state;
            _epoch = epoch;
        }

        public ProtoSafeInstrumentWriter BeginInstrument(scoped ReadOnlySpan<byte> symbol)
        {
            Guard();
            if (symbol.IsEmpty)
            {
                Fail();
                throw new ArgumentException("Required Symbol must not be empty.", nameof(symbol));
            }

            WriteSpan(55, symbol);
            Advance();
            return new ProtoSafeInstrumentWriter(_destination, _state, _state[1]);
        }

        private void Guard()
        {
            if (_state.Length < ProtoSafeWriterOwner.RequiredStateLength ||
                _state[0] != 0 ||
                _epoch == 0 ||
                _state[1] != _epoch)
            {
                throw new InvalidOperationException("Writer handle is default, stale, or failed.");
            }
        }

        private void WriteSpan(int tag, scoped ReadOnlySpan<byte> value)
        {
            try
            {
                int pos = _state[2];
                Wire.WriteSpan(_destination, ref pos, tag, value);
                _state[2] = pos;
            }
            catch (ArgumentException)
            {
                Fail();
                throw;
            }
        }

        private void Advance()
        {
            _state[1]++;
            _epoch = 0;
        }

        private void Fail()
        {
            if (_state.Length >= ProtoSafeWriterOwner.RequiredStateLength)
            {
                _state[0] = -1;
                _state[1]++;
            }
        }
    }

    public ref struct ProtoSafeInstrumentWriter
    {
        private Span<byte> _destination;
        private Span<int> _state;
        private int _epoch;

        internal ProtoSafeInstrumentWriter(Span<byte> destination, Span<int> state, int epoch)
        {
            _destination = destination;
            _state = state;
            _epoch = epoch;
        }

        public ProtoSafeTailWriter EndInstrument(char side, decimal orderQty, char ordType)
        {
            Guard();
            if (side != '1' && side != '2')
            {
                Fail();
                throw new ArgumentOutOfRangeException(nameof(side));
            }
            if (orderQty <= 0)
            {
                Fail();
                throw new ArgumentOutOfRangeException(nameof(orderQty));
            }
            if (ordType != '1' && ordType != '2')
            {
                Fail();
                throw new ArgumentOutOfRangeException(nameof(ordType));
            }

            try
            {
                int pos = _state[2];
                Wire.WriteChar(_destination, ref pos, 54, side);
                Wire.WriteDecimal(_destination, ref pos, 38, orderQty);
                Wire.WriteChar(_destination, ref pos, 40, ordType);
                _state[2] = pos;
            }
            catch (ArgumentException)
            {
                Fail();
                throw;
            }

            Advance();
            return new ProtoSafeTailWriter(_destination, _state, _state[1]);
        }

        private void Guard()
        {
            if (_state.Length < ProtoSafeWriterOwner.RequiredStateLength ||
                _state[0] != 0 ||
                _epoch == 0 ||
                _state[1] != _epoch)
            {
                throw new InvalidOperationException("Writer handle is default, stale, or failed.");
            }
        }

        private void Advance()
        {
            _state[1]++;
            _epoch = 0;
        }

        private void Fail()
        {
            if (_state.Length >= ProtoSafeWriterOwner.RequiredStateLength)
            {
                _state[0] = -1;
                _state[1]++;
            }
        }
    }

    public ref struct ProtoSafeTailWriter
    {
        private Span<byte> _destination;
        private Span<int> _state;
        private int _epoch;

        internal ProtoSafeTailWriter(Span<byte> destination, Span<int> state, int epoch)
        {
            _destination = destination;
            _state = state;
            _epoch = epoch;
        }

        // Optional component: omission is no call; once entered, its required child is mandatory.
        public ProtoSafeOptionalBrokerWriter BeginOptionalBroker(scoped ReadOnlySpan<byte> broker)
        {
            Guard();
            if (broker.IsEmpty)
            {
                Fail();
                throw new ArgumentException("ExecutingBroker is required when the component is present.", nameof(broker));
            }

            WriteSpan(76, broker);
            Advance();
            return new ProtoSafeOptionalBrokerWriter(_destination, _state, _state[1]);
        }

        public int Finish()
        {
            Guard();
            _state[0] = 1;
            _state[1]++;
            return _state[2];
        }

        private void Guard()
        {
            if (_state.Length < ProtoSafeWriterOwner.RequiredStateLength ||
                _state[0] != 0 ||
                _epoch == 0 ||
                _state[1] != _epoch)
            {
                throw new InvalidOperationException("Writer handle is default, stale, or failed.");
            }
        }

        private void WriteSpan(int tag, scoped ReadOnlySpan<byte> value)
        {
            try
            {
                int pos = _state[2];
                Wire.WriteSpan(_destination, ref pos, tag, value);
                _state[2] = pos;
            }
            catch (ArgumentException)
            {
                Fail();
                throw;
            }
        }

        private void Advance()
        {
            _state[1]++;
            _epoch = 0;
        }

        private void Fail()
        {
            if (_state.Length >= ProtoSafeWriterOwner.RequiredStateLength)
            {
                _state[0] = -1;
                _state[1]++;
            }
        }
    }

    public ref struct ProtoSafeOptionalBrokerWriter
    {
        private Span<byte> _destination;
        private Span<int> _state;
        private int _epoch;

        internal ProtoSafeOptionalBrokerWriter(Span<byte> destination, Span<int> state, int epoch)
        {
            _destination = destination;
            _state = state;
            _epoch = epoch;
        }

        public ProtoSafeTailWriter EndOptionalBroker()
        {
            if (_state.Length < ProtoSafeWriterOwner.RequiredStateLength ||
                _state[0] != 0 ||
                _epoch == 0 ||
                _state[1] != _epoch)
            {
                throw new InvalidOperationException("Component handle is default, stale, or failed.");
            }

            _state[1]++;
            _epoch = 0;
            return new ProtoSafeTailWriter(_destination, _state, _state[1]);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Reader side: reuses the eager-locate/lazy-parse single-scan shape already implemented by
    // ReaderEmitter.cs, plus a coherent first-occurrence selective projection policy.
    // ---------------------------------------------------------------------------------------

    public readonly ref struct ProtoOrderReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private readonly int _clOrdIdStart, _clOrdIdLength;
        private readonly int _sideStart;
        private readonly int _orderQtyStart, _orderQtyLength;
        private readonly int _ordTypeStart;
        private readonly int _priceStart, _priceLength;
        private readonly bool _pricePresent;

        public ProtoOrderReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _clOrdIdStart = 0; _clOrdIdLength = 0;
            _sideStart = 0;
            _orderQtyStart = 0; _orderQtyLength = 0;
            _ordTypeStart = 0;
            _priceStart = 0; _priceLength = 0;
            _pricePresent = false;

            int pos = 0;
            while (Wire.TryReadField(buffer, pos, out int tag, out int valueStart, out int valueLength, out int nextPos))
            {
                switch (tag)
                {
                    case 11: _clOrdIdStart = valueStart; _clOrdIdLength = valueLength; break; // last-wins on duplicates
                    case 54: _sideStart = valueStart; break;
                    case 38: _orderQtyStart = valueStart; _orderQtyLength = valueLength; break;
                    case 40: _ordTypeStart = valueStart; break;
                    case 44: _priceStart = valueStart; _priceLength = valueLength; _pricePresent = true; break;
                    // 55/48 (Instrument) and 453/448/447/452 (NoPartyIDs) belong to nested
                    // scopes and are intentionally NOT collected here (see Instrument/NoPartyIDs
                    // properties below) — matches ReaderEmitter.cs's existing convention.
                    // Any other tag (unknown/vendor extension) falls through here untouched.
                }

                pos = nextPos;
            }
        }

        public ReadOnlySpan<byte> ClOrdID => _buffer.Slice(_clOrdIdStart, _clOrdIdLength);

        public char Side => (char)_buffer[_sideStart];

        public decimal OrderQty => decimal.Parse(System.Text.Encoding.ASCII.GetString(_buffer.Slice(_orderQtyStart, _orderQtyLength)));

        public char OrdType => (char)_buffer[_ordTypeStart];

        public bool TryGetPrice(out decimal value)
        {
            if (!_pricePresent)
            {
                value = default;
                return false;
            }

            return Utf8Parser.TryParse(_buffer.Slice(_priceStart, _priceLength), out value, out _);
        }

        public ProtoInstrumentReader Instrument => new ProtoInstrumentReader(_buffer);

        public ProtoPartyGroupReader NoPartyIDs => new ProtoPartyGroupReader(_buffer);
    }

    public readonly ref struct ProtoInstrumentReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private readonly int _symbolStart, _symbolLength;
        private readonly int _securityIdStart, _securityIdLength;
        private readonly bool _securityIdPresent;

        public ProtoInstrumentReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _symbolStart = 0; _symbolLength = 0;
            _securityIdStart = 0; _securityIdLength = 0;
            _securityIdPresent = false;

            int pos = 0;
            while (Wire.TryReadField(buffer, pos, out int tag, out int valueStart, out int valueLength, out int nextPos))
            {
                switch (tag)
                {
                    case 55: _symbolStart = valueStart; _symbolLength = valueLength; break;
                    case 48: _securityIdStart = valueStart; _securityIdLength = valueLength; _securityIdPresent = true; break;
                }

                pos = nextPos;
            }
        }

        public ReadOnlySpan<byte> Symbol => _buffer.Slice(_symbolStart, _symbolLength);

        public bool TryGetSecurityID(out ReadOnlySpan<byte> value)
        {
            value = _buffer.Slice(_securityIdStart, _securityIdLength);
            return _securityIdPresent;
        }
    }

    public readonly ref struct ProtoPartyGroupReader
    {
        private readonly ReadOnlySpan<byte> _buffer;

        public ProtoPartyGroupReader(ReadOnlySpan<byte> buffer) => _buffer = buffer;

        /// <summary>Presence-aware accessor: distinguishes "tag 453 absent" from "tag 453=0" (docs/CONTRACT.md §12.2/§12.5).</summary>
        public bool TryGetCount(out int count)
        {
            int pos = 0;
            while (Wire.TryReadField(_buffer, pos, out int tag, out int valueStart, out int valueLength, out int nextPos))
            {
                if (tag == 453)
                {
                    return Utf8Parser.TryParse(_buffer.Slice(valueStart, valueLength), out count, out _);
                }

                pos = nextPos;
            }

            count = 0;
            return false;
        }

        /// <summary>Convenience accessor: absent and explicit-zero both collapse to 0 here — use <see cref="TryGetCount"/> when the distinction matters.</summary>
        public int Count => TryGetCount(out int count) ? count : 0;

        public Enumerator GetEnumerator()
        {
            // Entries begin right after the NUMINGROUP (tag 453) field. If 453 is absent
            // altogether (group never begun on the write side), start past the end of the
            // buffer so the first MoveNext() finds nothing — zero entries, same outward result
            // as an explicit "453=0", even though the two are distinguishable via TryGetCount
            // plus a raw byte inspection (see ScopedApiContractExamplesTests for that proof).
            int pos = 0;
            while (Wire.TryReadField(_buffer, pos, out int tag, out int valueStart, out int valueLength, out int nextPos))
            {
                if (tag == 453)
                {
                    int count = 0;
                    Utf8Parser.TryParse(_buffer.Slice(valueStart, valueLength), out count, out _);
                    return new Enumerator(_buffer, nextPos, count);
                }

                pos = nextPos;
            }

            return new Enumerator(_buffer, _buffer.Length, 0);
        }

        public ref struct Enumerator
        {
            private readonly ReadOnlySpan<byte> _buffer;
            private int _pos;
            private int _remaining;

            internal Enumerator(ReadOnlySpan<byte> buffer, int startPos, int count)
            {
                _buffer = buffer;
                _pos = startPos;
                _remaining = count < 0 ? 0 : count;
                Current = default;
            }

            // Current and copies of it remain valid while the underlying input span remains valid.
            // Advancing this enumerator does not invalidate an independently copied entry reader.
            public ProtoPartyEntryReader Current { get; private set; }

            public bool MoveNext()
            {
                if (_remaining <= 0)
                {
                    return false;
                }

                // Delimiter tag 448 (PartyID, the entry's first declared field) marks the start
                // of each new entry — known at compile time from the schema, no runtime lookup.
                if (!Wire.TryReadField(_buffer, _pos, out int tag, out _, out _, out _) || tag != 448)
                {
                    _remaining = 0;
                    return false;
                }

                int entryStart = _pos;
                Wire.TryReadField(_buffer, _pos, out _, out _, out _, out int cursor);
                while (Wire.TryReadField(_buffer, cursor, out int nextTag, out _, out _, out int afterNext))
                {
                    if (nextTag == 448 || !IsEntryMember(nextTag))
                    {
                        break;
                    }

                    cursor = afterNext;
                }

                Current = new ProtoPartyEntryReader(_buffer.Slice(entryStart, cursor - entryStart));
                _pos = cursor;
                _remaining--;
                return true;
            }

            private static bool IsEntryMember(int tag) =>
                tag == 448 || tag == 447 || tag == 452 ||
                tag == 802 || tag == 523 || tag == 803;
        }
    }

    public readonly ref struct ProtoPartyEntryReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private readonly int _partyIdStart, _partyIdLength;
        private readonly int _sourceStart;
        private readonly bool _sourcePresent;
        private readonly int _roleStart, _roleLength;
        private readonly bool _rolePresent;

        public ProtoPartyEntryReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _partyIdStart = 0; _partyIdLength = 0;
            _sourceStart = 0; _sourcePresent = false;
            _roleStart = 0; _roleLength = 0; _rolePresent = false;

            int pos = 0;
            while (Wire.TryReadField(buffer, pos, out int tag, out int valueStart, out int valueLength, out int nextPos))
            {
                switch (tag)
                {
                    case 448: _partyIdStart = valueStart; _partyIdLength = valueLength; break;
                    case 447: _sourceStart = valueStart; _sourcePresent = true; break;
                    case 452: _roleStart = valueStart; _roleLength = valueLength; _rolePresent = true; break;
                }

                pos = nextPos;
            }
        }

        public ReadOnlySpan<byte> PartyID => _buffer.Slice(_partyIdStart, _partyIdLength);

        public bool TryGetPartyIDSource(out char value)
        {
            value = _sourcePresent ? (char)_buffer[_sourceStart] : default;
            return _sourcePresent;
        }

        public bool TryGetPartyRole(out int value)
        {
            if (!_rolePresent)
            {
                value = default;
                return false;
            }

            return Utf8Parser.TryParse(_buffer.Slice(_roleStart, _roleLength), out value, out _);
        }

        public ProtoNestedPartyGroupReader NestedParties => new ProtoNestedPartyGroupReader(_buffer);
    }

    public readonly ref struct ProtoNestedPartyGroupReader
    {
        private readonly ReadOnlySpan<byte> _buffer;

        public ProtoNestedPartyGroupReader(ReadOnlySpan<byte> buffer) => _buffer = buffer;

        public Enumerator GetEnumerator()
        {
            int pos = 0;
            while (Wire.TryReadField(_buffer, pos, out int tag, out int valueStart, out int valueLength, out int next))
            {
                if (tag == 802)
                {
                    int count = 0;
                    Utf8Parser.TryParse(_buffer.Slice(valueStart, valueLength), out count, out _);
                    return new Enumerator(_buffer, next, count);
                }

                pos = next;
            }

            return new Enumerator(_buffer, _buffer.Length, 0);
        }

        public ref struct Enumerator
        {
            private readonly ReadOnlySpan<byte> _buffer;
            private int _position;
            private int _remaining;

            internal Enumerator(ReadOnlySpan<byte> buffer, int position, int count)
            {
                _buffer = buffer;
                _position = position;
                _remaining = count < 0 ? 0 : count;
                Current = default;
            }

            public ProtoNestedPartyEntryReader Current { get; private set; }

            public bool MoveNext()
            {
                if (_remaining <= 0 ||
                    !Wire.TryReadField(_buffer, _position, out int tag, out _, out _, out int cursor) ||
                    tag != 523)
                {
                    _remaining = 0;
                    return false;
                }

                int start = _position;
                while (Wire.TryReadField(_buffer, cursor, out int nextTag, out _, out _, out int next))
                {
                    if (nextTag == 523 || (nextTag != 803))
                    {
                        break;
                    }

                    cursor = next;
                }

                Current = new ProtoNestedPartyEntryReader(_buffer.Slice(start, cursor - start));
                _position = cursor;
                _remaining--;
                return true;
            }
        }
    }

    public readonly ref struct ProtoNestedPartyEntryReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private readonly int _idStart;
        private readonly int _idLength;

        public ProtoNestedPartyEntryReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _idStart = 0;
            _idLength = 0;
            Wire.TryReadField(buffer, 0, out _, out _idStart, out _idLength, out _);
        }

        public ReadOnlySpan<byte> NestedPartyID => _buffer.Slice(_idStart, _idLength);
    }

    /// <summary>
    /// Selective-projection spike (reuses the <c>[FixView]</c> early-exit scan model —
    /// FixViewEmitter.cs — docs/CONTRACT.md §11/§12.6): only ClOrdID and Price are located: the
    /// scan stops as soon as both have been seen, instead of scanning every field in the message.
    /// </summary>
    public readonly ref struct ProtoOrderView
    {
        private readonly int _clOrdIdStart, _clOrdIdLength;
        private readonly int _priceStart, _priceLength;
        private readonly bool _pricePresent;

        public ProtoOrderView(ReadOnlySpan<byte> buffer)
        {
            _clOrdIdStart = 0; _clOrdIdLength = 0;
            _priceStart = 0; _priceLength = 0;
            _pricePresent = false;

            bool haveClOrdId = false;
            bool havePrice = false; // "have" here means "resolved", not "present" - Price is optional
            int remaining = 2;
            int pos = 0;
            while (remaining > 0 && Wire.TryReadField(buffer, pos, out int tag, out int valueStart, out int valueLength, out int nextPos))
            {
                if (tag == 11 && !haveClOrdId)
                {
                    _clOrdIdStart = valueStart;
                    _clOrdIdLength = valueLength;
                    haveClOrdId = true;
                    remaining--;
                }
                else if (tag == 44 && !havePrice)
                {
                    _priceStart = valueStart;
                    _priceLength = valueLength;
                    _pricePresent = true;
                    havePrice = true;
                    remaining--;
                }

                pos = nextPos;
            }

            _clOrdIdBuffer = buffer;
        }

        private readonly ReadOnlySpan<byte> _clOrdIdBuffer;

        public ReadOnlySpan<byte> ClOrdID => _clOrdIdBuffer.Slice(_clOrdIdStart, _clOrdIdLength);

        public bool TryGetPrice(out decimal value)
        {
            if (!_pricePresent)
            {
                value = default;
                return false;
            }

            return Utf8Parser.TryParse(_clOrdIdBuffer.Slice(_priceStart, _priceLength), out value, out _);
        }
    }
}
