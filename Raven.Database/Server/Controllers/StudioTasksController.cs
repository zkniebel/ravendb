﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json;
using Raven.Abstractions.Commands;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Json;
using Raven.Abstractions.Smuggler;
using Raven.Client.Indexes;
using Raven.Client.Util;
using Raven.Database.Smuggler;
using Raven.Json.Linq;
using Raven.Database.Extensions;
using System.Text.RegularExpressions;
using System.Reflection;

namespace Raven.Database.Server.Controllers
{
	public class StudioTasksController : RavenDbApiController
	{
        const int csvImportBatchSize = 512;

		[HttpPost]
		[Route("studio-tasks/import")]
		[Route("databases/{databaseName}/studio-tasks/import")]
		public async Task<HttpResponseMessage> ImportDatabase(int batchSize, bool includeExpiredDocuments, ItemType operateOnTypes, string filtersPipeDelimited, string transformScript)
		{
            if (!Request.Content.IsMimeMultipartContent())
            {
                throw new HttpResponseException(HttpStatusCode.UnsupportedMediaType);
            }

            var streamProvider = new MultipartMemoryStreamProvider();
            await Request.Content.ReadAsMultipartAsync(streamProvider);
            var fileStream = await streamProvider.Contents
                .First(c => c.Headers.ContentDisposition.Name == "\"file\"")
                .ReadAsStreamAsync();

            var dataDumper = new DataDumper(Database);
            var importOptions = new SmugglerImportOptions
            {
                FromStream = fileStream
            };
            var options = new SmugglerOptions
            {
                BatchSize = batchSize,
                ShouldExcludeExpired = includeExpiredDocuments,
                OperateOnTypes = operateOnTypes,
                TransformScript = transformScript
            };

            // Filters are passed in without the aid of the model binder. Instead, we pass in a list of FilterSettings using a string like this: pathHere;;;valueHere;;;true|||againPathHere;;;anotherValue;;;false
            // Why? Because I don't see a way to pass a list of a values to a WebAPI method that accepts a file upload, outside of passing in a simple string value and parsing it ourselves.
            if (filtersPipeDelimited != null)
            {
                options.Filters.AddRange(filtersPipeDelimited
                    .Split(new string[] { "|||" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Split(new string[] { ";;;" }, StringSplitOptions.RemoveEmptyEntries))
                    .Select(o => new FilterSetting
                    {
                        Path = o[0],
                        Values = new List<string> { o[1] },
                        ShouldMatch = bool.Parse(o[2])
                    }));
            }

            await dataDumper.ImportData(importOptions, options);
            return GetEmptyMessage();
		}

	    public class ExportData
	    {
            public string SmugglerOptions { get; set; }
	    }
        
		[HttpPost]
		[Route("studio-tasks/exportDatabase")]
		[Route("databases/{databaseName}/studio-tasks/exportDatabase")]
        public async Task<HttpResponseMessage> ExportDatabase(ExportData smugglerOptionsJson)
		{
            var requestString = smugglerOptionsJson.SmugglerOptions;
	        SmugglerOptions smugglerOptions;
      
            using (var jsonReader = new RavenJsonTextReader(new StringReader(requestString)))
			{
				var serializer = JsonExtensions.CreateDefaultJsonSerializer();
                smugglerOptions = (SmugglerOptions)serializer.Deserialize(jsonReader, typeof(SmugglerOptions));
			}


            var result = GetEmptyMessage();
            
            // create PushStreamContent object that will be called when the output stream will be ready.
			result.Content = new PushStreamContent(async (outputStream, content, arg3) =>
			{
			    try
			    {
			        await new DataDumper(Database).ExportData(new SmugglerExportOptions
			        {
			            ToStream = outputStream
			        }, smugglerOptions).ConfigureAwait(false);
			    }
                    // close the output stream, so the PushStremContent mechanism will know that the process is finished
			    finally
			    {
			        outputStream.Close();
			    }

				
			});

            result.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = string.Format("Dump of {0}, {1}.ravendump", this.DatabaseName, DateTime.Now.ToString("dd MM yyyy HH-mm", CultureInfo.InvariantCulture))
            };
			
			return result;
		}
        
		[HttpPost]
		[Route("studio-tasks/createSampleData")]
		[Route("databases/{databaseName}/studio-tasks/createSampleData")]
		public async Task<HttpResponseMessage> CreateSampleData()
		{
			var results = Database.Queries.Query(Constants.DocumentsByEntityNameIndex, new IndexQuery(), CancellationToken.None);
			if (results.Results.Count > 0)
			{
				return GetMessageWithString("You cannot create sample data in a database that already contains documents", HttpStatusCode.BadRequest);
			}

			using (var sampleData = typeof(StudioTasksController).Assembly.GetManifestResourceStream("Raven.Database.Server.Assets.EmbeddedData.Northwind.dump"))
			{
				var smugglerOptions = new SmugglerOptions
				{
					OperateOnTypes = ItemType.Documents | ItemType.Indexes | ItemType.Transformers,
					ShouldExcludeExpired = false,
				};
				var dataDumper = new DataDumper(Database);
				await dataDumper.ImportData(new SmugglerImportOptions {FromStream = sampleData}, smugglerOptions);
			}

			return GetEmptyMessage();
		}

        [HttpGet]
        [Route("studio-tasks/createSampleDataClass")]
        [Route("databases/{databaseName}/studio-tasks/createSampleDataClass")]
        public async Task<HttpResponseMessage> CreateSampleDataClass()
        {
            using (var sampleData = typeof(StudioTasksController).Assembly.GetManifestResourceStream("Raven.Database.Server.Assets.EmbeddedData.NorthwindHelpData.cs"))
            {
                if (sampleData == null)
                    return GetEmptyMessage();
                   
                sampleData.Position = 0;
                using (var reader = new StreamReader(sampleData, Encoding.UTF8))
                {
                   var data = reader.ReadToEnd();
                   return GetMessageWithObject(data);
                }
            }
        }

		[HttpGet]
		[Route("databases/{databaseName}/studio-tasks/generateCSharpIndexDefinition/{*fullIndexName}")]
		public HttpResponseMessage GenerateCSharpIndexDefinition(string fullIndexName)
		{
			var indexDefinition = Database.Indexes.GetIndexDefinition(fullIndexName);
			if (indexDefinition == null)
				return GetEmptyMessage(HttpStatusCode.NotFound);

			var indexName = fullIndexName.Replace("/", string.Empty);
			var mapList = indexDefinition.Maps.Select(mapString => @"@""" + mapString + @"""").ToList();
			var maps = string.Join(", ", mapList);
			var reduce = indexDefinition.Reduce != null ? @"@""" + indexDefinition.Reduce + @"""" : "null";
			var maxIndexOutputsPerDocument = indexDefinition.MaxIndexOutputsPerDocument != null ? 
												indexDefinition.MaxIndexOutputsPerDocument.ToString() : "null";

			var indexes = GenerateStringFromStringToEnumDictionary(indexDefinition.Indexes);
			var stores = GenerateStringFromStringToEnumDictionary(indexDefinition.Stores);
			var sortOptions = GenerateStringFromStringToEnumDictionary(indexDefinition.SortOptions);
			var termVectors = GenerateStringFromStringToEnumDictionary(indexDefinition.TermVectors);

			var analyzersList = (from analyzer in indexDefinition.Analyzers
								select @"{ """ + analyzer.Key + @""", """ + analyzer.Value + @""" }").ToList();
			var analyzers = ConvertListToStringWithSeperator(analyzersList);

			var suggestionList = (from suggestion in indexDefinition.Suggestions
								  let distance = "Distance = StringDistanceTypes." + suggestion.Value.Distance
								  let accuracy = "Accuracy = " + suggestion.Value.Accuracy
								  select @"{ """ + suggestion.Key + @""", new SuggestionOptions { " + distance + ", " + accuracy + "f } }").ToList();
			var suggestions = ConvertListToStringWithSeperator(suggestionList);

			var spatialIndexesList = (from spatialIndex in indexDefinition.SpatialIndexes
									  let type = "Type = SpatialFieldType." + spatialIndex.Value.Type
									  let strategy = "Strategy = SpatialSearchStrategy." + spatialIndex.Value.Strategy
									  let maxTreeLevel = "MaxTreeLevel = " + spatialIndex.Value.MaxTreeLevel
									  let minX = "MinX = " + spatialIndex.Value.MinX
									  let maxX = "MaxX = " + spatialIndex.Value.MaxX
									  let minY = "MinY = " + spatialIndex.Value.MinY
									  let maxY = "MaxY = " + spatialIndex.Value.MaxY
									  let units = "Units = SpatialUnits." + spatialIndex.Value.Units
									  let spatialOptions = string.Join(", ", type, strategy, maxTreeLevel, minX, maxX, minY, maxY, units)
									  select @"{ """ + spatialIndex.Key + @""", new SpatialOptions { " + spatialOptions + " } }").ToList();
			var spatialIndexes = ConvertListToStringWithSeperator(spatialIndexesList);

			var cSharpCode = 
				@"public class " + indexName + @" : AbstractIndexCreationTask
				{
					public override string IndexName
					{
						get { return """ + indexName + @"""; }
					}

					public override IndexDefinition CreateIndexDefinition()
					{
						return new IndexDefinition
						{
							Maps = { " + maps + @" },
							Reduce = " + reduce + @",
							MaxIndexOutputsPerDocument = " + maxIndexOutputsPerDocument + @",
							Indexes = " + indexes + @",
							Stores = " + stores + @",
							TermVectors = " + termVectors + @",
							SortOptions = " + sortOptions + @",
							Analyzers = " + analyzers + @",
							Suggestions = " + suggestions + @",
							SpatialIndexes = " + spatialIndexes + @"
						};
					}
				}";

			return GetMessageWithObject(cSharpCode);
		}

		private static string GenerateStringFromStringToEnumDictionary<T>(IEnumerable<KeyValuePair<string, T>> dictionary)
		{
			var list = (from keyValuePair in dictionary
						let value = keyValuePair.Value.GetType().Name + "." + keyValuePair.Value
						select @"{ """ + keyValuePair.Key + @""", " + value + " }").ToList();

			return ConvertListToStringWithSeperator(list);
		}

		private static string ConvertListToStringWithSeperator(List<string> list)
		{
			return (list.Count > 0) ? "{ " + string.Join(", ", list) + @" }" : "null";
		}

        [HttpGet]
        [Route("studio-tasks/new-encryption-key")]
        public HttpResponseMessage GetNewEncryption(string path = null)
        {
            RandomNumberGenerator randomNumberGenerator = new RNGCryptoServiceProvider();
            var byteStruct = new byte[Constants.DefaultGeneratedEncryptionKeyLength];
            randomNumberGenerator.GetBytes(byteStruct);
            var result = Convert.ToBase64String(byteStruct);

            HttpResponseMessage response = Request.CreateResponse(HttpStatusCode.OK, result);
            return response;
        }

        [HttpPost]
        [Route("studio-tasks/is-base-64-key")]
        public async Task<HttpResponseMessage> IsBase64Key(string path = null)
        {
            string message = null;
            try
            {
                //Request is of type HttpRequestMessage
                string keyObjectString = await Request.Content.ReadAsStringAsync();
                NameValueCollection nvc = HttpUtility.ParseQueryString(keyObjectString);
                var key = nvc["key"];

                //Convert base64-encoded hash value into a byte array.
                //ReSharper disable once ReturnValueOfPureMethodIsNotUsed
                Convert.FromBase64String(key);
            }
            catch (Exception e)
            {
				message = "The key must be in Base64 encoding format!";
            }

			HttpResponseMessage response = Request.CreateResponse((message == null) ? HttpStatusCode.OK : HttpStatusCode.BadRequest, message);
            return response;
        }

        async Task FlushBatch(IEnumerable<RavenJObject> batch)
        {
            var sw = Stopwatch.StartNew();

            var commands = (from doc in batch
                            let metadata = doc.Value<RavenJObject>("@metadata")
                            let removal = doc.Remove("@metadata")
                            select new PutCommandData
                            {
                                Metadata = metadata,
                                Document = doc,
                                Key = metadata.Value<string>("@id"),
                            }).ToArray();

            Database.Batch(commands, CancellationToken.None);
        }

	    [HttpPost]
        [Route("studio-tasks/loadCsvFile")]
	    [Route("databases/{databaseName}/studio-tasks/loadCsvFile")]
	    public async Task<HttpResponseMessage> LoadCsvFile()
	    {

            if (!Request.Content.IsMimeMultipartContent())
                throw new Exception(); // divided by zero

            var provider = new MultipartMemoryStreamProvider();
            await Request.Content.ReadAsMultipartAsync(provider);

            foreach (var file in provider.Contents)
            {
                var filename = file.Headers.ContentDisposition.FileName.Trim('\"');

                var stream = await file.ReadAsStreamAsync();

                using (var csvReader = new TextFieldParser(stream))
                {
	                csvReader.SetDelimiters(",");
                    var headers = csvReader.ReadFields();
                    var entity =
                        Inflector.Pluralize(CSharpClassName.ConvertToValidClassName(Path.GetFileNameWithoutExtension(filename)));
                    if (entity.Length > 0 && char.IsLower(entity[0]))
                        entity = char.ToUpper(entity[0]) + entity.Substring(1);

                    var totalCount = 0;
                    var batch = new List<RavenJObject>();
                    var columns = headers.Where(x => x.StartsWith("@") == false).ToArray();

                    batch.Clear();
	                while (csvReader.EndOfData == false)
	                {
		                var record = csvReader.ReadFields();
                        var document = new RavenJObject();
                        string id = null;
                        RavenJObject metadata = null;
		                for (int index = 0; index < columns.Length; index++)
		                {
			                var column = columns[index];
			                if (string.IsNullOrEmpty(column))
				                continue;

			                if (string.Equals("id", column, StringComparison.OrdinalIgnoreCase))
			                {
								id = record[index];
			                }
			                else if (string.Equals(Constants.RavenEntityName, column, StringComparison.OrdinalIgnoreCase))
			                {
				                metadata = metadata ?? new RavenJObject();
								metadata[Constants.RavenEntityName] = record[index];
								id = id ?? record[index] + "/";
			                }
			                else if (string.Equals(Constants.RavenClrType, column, StringComparison.OrdinalIgnoreCase))
			                {
				                metadata = metadata ?? new RavenJObject();
								metadata[Constants.RavenClrType] = record[index];
								id = id ?? record[index] + "/";
			                }
			                else
			                {
								document[column] = SetValueInDocument(record[index]);
			                }
		                }

		                metadata = metadata ?? new RavenJObject { { "Raven-Entity-Name", entity } };
                        document.Add("@metadata", metadata);
                        metadata.Add("@id", id ?? Guid.NewGuid().ToString());

                        batch.Add(document);
                        totalCount++;

                        if (batch.Count >= csvImportBatchSize)
                        {
                            await FlushBatch(batch);
                            batch.Clear();
                        }
                    }

                    if (batch.Count > 0)
                    {
                        await FlushBatch(batch);
                    }
                }

            }

            return GetEmptyMessage();
	    }

		private static RavenJToken SetValueInDocument(string value)
		{
			if (string.IsNullOrEmpty(value))
				return value;

			var ch = value[0];
			if (ch == '[' || ch == '{')
			{
				try
				{
					return RavenJToken.Parse(value);
				}
				catch (Exception)
				{
					// ignoring failure to parse, will proceed to insert as a string value
				}
			}
			else if (char.IsDigit(ch) || ch == '-' || ch == '.')
			{
				// maybe it is a number?
				long longResult;
				if (long.TryParse(value, out longResult))
				{
					return longResult;
				}

				decimal decimalResult;
				if (decimal.TryParse(value, out decimalResult))
				{
					return decimalResult;
				}
			}
			else if (ch == '"' && value.Length > 1 && value[value.Length - 1] == '"')
			{
				return value.Substring(1, value.Length - 2);
			}

			return value;
		}
	}
}

