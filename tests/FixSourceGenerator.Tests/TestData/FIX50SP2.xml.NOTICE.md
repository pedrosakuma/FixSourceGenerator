`FIX50SP2.xml` in this directory is the public FIX 5.0 Service Pack 2 DataDictionary
from the [QuickFIX](https://github.com/quickfix/quickfix) project
(`spec/FIX50SP2.xml`, BSD-licensed), used here as the largest/most complex real-world
conformance fixture available (156 messages, 6000+ fields, 725 components) to guard
against codegen regressions like issue #11 across a wide variety of real dictionary
constructs. No proprietary or exchange-specific schema data is included.
