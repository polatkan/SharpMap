﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

namespace UnitTests.Data.Providers
{
    [TestFixture]
    public class PostGisTests
    {
        private string GetTestFile()
        {
            return Path.Combine(GetPathToTestDataDir(), "roads_ugl.shp");
        }

        private string GetPathToTestDataDir()
        {
            return Path.Combine(Path.GetDirectoryName(this.GetType().Assembly.CodeBase.Replace("file:///", "")), @"TestData\");
        }

        private List<uint> _insertedIds = new List<uint>(100);

        [TestFixtureSetUp]
        public void FixtureSetup()
        {
            var connStrBuilder = new NpgsqlConnectionStringBuilder(Properties.Settings.Default.PostGis);
            if (string.IsNullOrEmpty(connStrBuilder.Host) || string.IsNullOrEmpty(connStrBuilder.Database))
            {
                Assert.Ignore("Requires PostgreSQL connectionstring");
            }


            GeoAPI.GeometryServiceProvider.Instance = new NetTopologySuite.NtsGeometryServices();
            try
            {
                // Set up sample table
                using (var conn = new NpgsqlConnection(Properties.Settings.Default.PostGis))
                {
                    conn.Open();
                    // Load data
                    using (var shapeFile = new SharpMap.Data.Providers.ShapeFile(GetTestFile(), false, false, 4326))
                    {
                        shapeFile.Open();

                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText =
                                "SELECT COUNT(*) FROM \"geometry_columns\" WHERE \"f_table_name\" = 'roads_ugl_g' AND \"f_geometry_column\"='geom';";
                            var count = cmd.ExecuteScalar();
                            if (Convert.ToInt32(count) > 0)
                            {
                                cmd.CommandText = "SELECT DropGeometryColumn('roads_ugl_g', 'geom');";
                                cmd.ExecuteNonQuery();
                                cmd.CommandText = "DROP TABLE roads_ugl_g";
                                cmd.ExecuteNonQuery();
                            }

                            // The ID column cannot simply be int, because that would cause GetOidsInView to fail. The provider internally works with uint
                            cmd.CommandText =
                                "CREATE TABLE roads_ugl_g(id integer primary key, name character varying(100));";
                            cmd.ExecuteNonQuery();
                            cmd.CommandText = "SELECT AddGeometryColumn('roads_ugl_g', 'geom', " + shapeFile.SRID +
                                              ", 'GEOMETRY', 2);";
                            cmd.ExecuteNonQuery();
                        }


                        IEnumerable<uint> indexes = shapeFile.GetOidsInView(shapeFile.GetExtents());

                        _insertedIds = new List<uint>(indexes.Take(100));
                        using (NpgsqlCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText =
                                "INSERT INTO roads_ugl_g(id, name, geom) VALUES (@PId, @PName, @PGeom);";
                            var @params = cmd.Parameters;
                            @params.AddRange(
                                new[]
                                    {
                                        new NpgsqlParameter("PId", NpgsqlDbType.Integer),
                                        new NpgsqlParameter("PName", NpgsqlDbType.Varchar, 100),
                                        new NpgsqlParameter("PGeom", NpgsqlDbType.Bytea)
                                    });

                            var writer = new PostGisWriter();

                            foreach (var idx in _insertedIds)
                            {
                                var feature = shapeFile.GetFeatureByOid(idx);

                                @params["PId"].NpgsqlValue = (int) idx;
                                @params["PName"].NpgsqlValue = feature.Attributes["NAME"];
                                @params["PGeom"].NpgsqlValue = writer.Write(feature.Geometry);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                }
            }
            catch
            {
                Assert.Ignore("Failed to connect to PostgreSQL/PostGIS Server");
            }

        }

        [TestFixtureTearDown]
        public void FixtureTearDown()
        {
            var connStrBuilder = new NpgsqlConnectionStringBuilder(Properties.Settings.Default.PostGis);
            if (string.IsNullOrEmpty(connStrBuilder.Host) || string.IsNullOrEmpty(connStrBuilder.Database))
            {
                return;
            }

            try
            {
                // Drop sample table
                using (var conn = new NpgsqlConnection(Properties.Settings.Default.PostGis))
                {
                    conn.Open();
                    using (NpgsqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT DropGeometryColumn('roads_ugl_g', 'geom');";
                        cmd.ExecuteNonQuery();
                        cmd.CommandText = "DROP TABLE roads_ugl_g";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch { }
        }

        private static SharpMap.Data.Providers.PostGIS GetTestProvider()
        {
            return new SharpMap.Data.Providers.PostGIS(Properties.Settings.Default.PostGis, "roads_ugl_g", "geom", "id");
        }

        /// <summary>
        /// Get the envelope of the entire roads_ugl file
        /// </summary>
        private static GeoAPI.Geometries.Envelope GetTestEnvelope()
        {
            return SharpMap.Converters.WellKnownText.GeometryFromWKT.Parse("POLYGON ((-97.23724071609665 41.698023105763589, -82.424263624596563 41.698023105763589, -82.424263624596563 49.000629000758515, -97.23724071609665 49.000629000758515, -97.23724071609665 41.698023105763589))").EnvelopeInternal;
        }

        [Test]
        public void TestGetExtents()
        {
            var sq = GetTestProvider();

            GeoAPI.Geometries.Envelope extents = sq.GetExtents();

            Assert.IsNotNull(extents);
        }

        [Test]
        public void TestGetGeometriesInView()
        {
            var sq = GetTestProvider();

            var geometries = sq.GetGeometriesInView(GetTestEnvelope());

            Assert.IsNotNull(geometries);
            Assert.AreEqual(100, geometries.Count());
        }

        [Test]
        public void TestGetGeometriesInViewDefinitionQuery()
        {
            var sq = GetTestProvider();

            sq.DefinitionQuery = "NAME LIKE 'A%'";

            var geometries = sq.GetGeometriesInView(GetTestEnvelope());

            Assert.IsNotNull(geometries);
            Assert.LessOrEqual(geometries.Count(), 100);
        }

        [Test]
        public void TestGetObjectIDsInView()
        {
            var sq = GetTestProvider();

            var objectIds = sq.GetOidsInView(GetTestEnvelope());

            Assert.IsNotNull(objectIds);
            Assert.AreEqual(100, objectIds.Count());
        }

        [Test]
        public void TestExecuteIntersectionQuery()
        {
            var sq = GetTestProvider();

            var ds = new SharpMap.Data.FeatureDataSet();

            sq.ExecuteIntersectionQuery(GetTestEnvelope(), ds);

            Assert.AreEqual(100, ds.Tables[0].Rows.Count);
        }

        [Test]
        public void TestGetFeatureCount()
        {
            var sq = GetTestProvider();

            int count = sq.GetFeatureCount();

            Assert.AreEqual(100, count);
        }

        [Test]
        public void TestGetFeatureCountWithDefinitionQuery()
        {
            var sq = GetTestProvider();

            sq.DefinitionQuery = "NAME LIKE 'A%'";

            int count = sq.GetFeatureCount();

            Assert.LessOrEqual(count, 100);
        }

        [Test]
        public void TestGetFeature()
        {
            var sq = GetTestProvider();
            var rnd = new Random();
            for (var i = 0; i < 10; i++)
            {
                var feature = sq.GetFeatureByOid(_insertedIds[rnd.Next(0, 100)]);

                Assert.IsNotNull(feature);
            }
        }

        [Test]
        public void TestGetGeometryByOid()
        {
            var sq = GetTestProvider();
            var rnd = new Random();
            for (var i = 0; i < 10; i++)
            {
                var feature = sq.GetGeometryByOid(_insertedIds[rnd.Next(0, 100)]);

                Assert.IsNotNull(feature);
            }
        }

        [Test]
        public void TestGetFeatureNonExisting()
        {
            var sq = GetTestProvider();

            var feature = sq.GetFeatureByOid(99999999);

            Assert.IsNull(feature);
        }
    }
}