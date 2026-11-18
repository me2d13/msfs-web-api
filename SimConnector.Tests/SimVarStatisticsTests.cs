using System;
using System.Linq;
using Xunit;

namespace SimConnector.Tests
{
    public class SimVarStatisticsTests
    {
        private static SimVarReference Reference(string name, string unit = "", bool clientData = false)
        {
            return new SimVarReference { SimVarName = name, Unit = unit, IsClientData = clientData };
        }

        private static SimVarReference Value(SimVarReference reference, double value)
        {
            return reference with { Value = value };
        }

        [Fact]
        public void CountsReadsAndWritesPerVariable()
        {
            var statistics = new SimVarStatistics();
            var altitude = Reference("PLANE ALTITUDE", "feet");

            statistics.RecordRead(altitude, Value(altitude, 1166.9));
            statistics.RecordRead(altitude, Value(altitude, 1170.0));
            statistics.RecordWrite(altitude, Value(altitude, 5000));

            var entry = Assert.Single(statistics.Snapshot());
            Assert.Equal("PLANE ALTITUDE[feet]", entry.Name);
            Assert.Equal("SimVar", entry.Kind);
            Assert.Equal(2, entry.Reads);
            Assert.Equal(1, entry.Writes);
            Assert.Equal(0, entry.NoValue);
            Assert.Equal(5000, entry.LastValue);
            Assert.Equal(2, statistics.TotalReads);
            Assert.Equal(1, statistics.TotalWrites);
            Assert.Equal(0, statistics.TotalNoValue);
        }

        [Fact]
        public void ReadWithoutAValueIsCountedAndKeepsTheLastValue()
        {
            var statistics = new SimVarStatistics();
            var reference = Reference("PLANE ALTITUDE", "feet");

            statistics.RecordRead(reference, Value(reference, 1200));
            statistics.RecordRead(reference, reference with { Value = double.NaN }); // SimConnect timeout
            statistics.RecordRead(reference, null);                                  // not connected

            var entry = Assert.Single(statistics.Snapshot());
            Assert.Equal(3, entry.Reads);
            Assert.Equal(2, entry.NoValue);
            Assert.Equal(1200, entry.LastValue);
            Assert.Equal(2, statistics.TotalNoValue);
        }

        [Fact]
        public void FailedWriteIsCountedAndDoesNotChangeTheLastValue()
        {
            var statistics = new SimVarStatistics();
            var reference = Reference("L:VC_AT_ARM_LIGHT_VAL");

            statistics.RecordRead(reference, Value(reference, 0));
            statistics.RecordWrite(reference, null); // timed out waiting for confirmation

            var entry = Assert.Single(statistics.Snapshot());
            Assert.Equal(1, entry.Writes);
            Assert.Equal(1, entry.NoValue);
            Assert.Equal(0, entry.LastValue);
        }

        private static SimVarReference ClientField(string area, int offset, string typeName, uint sizeOrType)
        {
            // For client data the parser puts the area name in SimVarName and keeps the field apart.
            return new SimVarReference
            {
                SimVarName = area,
                IsClientData = true,
                ClientOffset = offset,
                ClientSizeOrType = sizeOrType,
                ClientTypeName = typeName,
            };
        }

        [Fact]
        public void ClientDataReferencesAreLabelledAndKeptApartFromSimVars()
        {
            var statistics = new SimVarStatistics();
            var area = ClientField("PMDG_NG3_Data", 0x1B5, "bool", 1);

            statistics.RecordRead(area, Value(area, 0));
            statistics.RecordRead(Reference("IRS_SEL"), Value(Reference("IRS_SEL"), 3));

            var snapshot = statistics.Snapshot();
            var clientEntry = snapshot.Single(e => e.Name.StartsWith("CLIENT:", StringComparison.Ordinal));
            Assert.Equal("CLIENT:PMDG_NG3_Data:0x1B5:bool", clientEntry.Name);
            Assert.Equal("Client data", clientEntry.Kind);
            Assert.Equal("SimVar", snapshot.Single(e => e.Name == "IRS_SEL").Kind);
        }

        [Fact]
        public void TwoFieldsOfOneClientAreaAreCountedSeparately()
        {
            var statistics = new SimVarStatistics();
            var irs = ClientField("PMDG_NG3_Data", 0x00, "byte", 2);
            var mcp = ClientField("PMDG_NG3_Data", 0x1A03, "float64", 7);

            statistics.RecordRead(irs, Value(irs, 3));
            statistics.RecordRead(mcp, irs with { Value = double.NaN });

            var snapshot = statistics.Snapshot();
            Assert.Equal(2, snapshot.Count);
            Assert.Equal(1, snapshot.Single(e => e.Name == "CLIENT:PMDG_NG3_Data:0x0:byte").Reads);
            Assert.Equal(1, snapshot.Single(e => e.Name == "CLIENT:PMDG_NG3_Data:0x1A03:float64").NoValue);
        }

        [Fact]
        public void MalformedClientReferenceKeepsItsPlaceholderOffsetInTheName()
        {
            var statistics = new SimVarStatistics();
            var invalid = ClientField("BOGUS_AREA", -1, "invalid", 0);

            statistics.RecordRead(invalid, null);

            var entry = Assert.Single(statistics.Snapshot());
            Assert.Equal("CLIENT:BOGUS_AREA:-1:invalid", entry.Name);
            Assert.Equal(1, entry.NoValue);
        }

        [Fact]
        public void SameNameInTwoUnitsIsCountedSeparately()
        {
            var statistics = new SimVarStatistics();
            var knots = Reference("AIRSPEED INDICATED", "knots");
            var kmh = Reference("AIRSPEED INDICATED", "km/h");

            statistics.RecordRead(knots, Value(knots, 120));
            statistics.RecordRead(knots, Value(knots, 121));
            statistics.RecordRead(kmh, Value(kmh, 224));

            var snapshot = statistics.Snapshot();
            Assert.Equal(2, snapshot.Count);
            Assert.Equal(2, snapshot.Single(e => e.Name == "AIRSPEED INDICATED[knots]").Reads);
            Assert.Equal(1, snapshot.Single(e => e.Name == "AIRSPEED INDICATED[km/h]").Reads);
            // Most used first.
            Assert.Equal("AIRSPEED INDICATED[knots]", snapshot[0].Name);
        }

        [Fact]
        public void SnapshotOrdersByActivityThenByName()
        {
            var statistics = new SimVarStatistics();
            foreach (var name in new[] { "ZULU", "ALPHA" })
            {
                var reference = Reference(name);
                statistics.RecordRead(reference, Value(reference, 1));
            }

            var busy = Reference("BUSY");
            for (var i = 0; i < 5; i++) statistics.RecordRead(busy, Value(busy, i));

            var snapshot = statistics.Snapshot();
            Assert.Equal(new[] { "BUSY", "ALPHA", "ZULU" }, snapshot.Select(e => e.Name).ToArray());
        }

        [Fact]
        public void NullReferenceIsIgnored()
        {
            var statistics = new SimVarStatistics();

            statistics.RecordRead(null, null);
            statistics.RecordWrite(null, null);

            Assert.Empty(statistics.Snapshot());
            Assert.Equal(0, statistics.TotalReads);
            Assert.Equal(0, statistics.TotalWrites);
        }

        [Fact]
        public void ResetDropsEverything()
        {
            var statistics = new SimVarStatistics();
            var reference = Reference("PLANE ALTITUDE", "feet");
            statistics.RecordRead(reference, Value(reference, 1));

            statistics.Reset();

            Assert.Empty(statistics.Snapshot());
            Assert.Equal(0, statistics.TotalReads);
            Assert.Equal(0, statistics.TotalNoValue);
        }

        [Theory]
        [InlineData("PLANE ALTITUDE", "", "PLANE ALTITUDE")]
        [InlineData("PLANE ALTITUDE", "feet", "PLANE ALTITUDE[feet]")]
        public void KeyOfUsesNameAndUnit(string name, string unit, string expected)
        {
            Assert.Equal(expected, SimVarStatistics.KeyOf(Reference(name, unit)));
        }
    }
}
