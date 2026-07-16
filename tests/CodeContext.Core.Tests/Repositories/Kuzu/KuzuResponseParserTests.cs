using System;
using System.Collections.Generic;
using System.Text.Json;
using CodeContext.Core.Repositories.Kuzu;
using CodeContext.Core.Serialization;
using Xunit;

namespace CodeContext.Core.Tests.Repositories.Kuzu
{
    using NodeDto = CodeContext.Core.Serialization.NodeDto;
    using IReadOnlyListNodeDto = System.Collections.Generic.IReadOnlyList<CodeContext.Core.Serialization.NodeDto>;
    using DictionaryStringObject = System.Collections.Generic.Dictionary<string, object>;
    
    public class KuzuResponseParserTests
    {
        [Fact]
        public void ParseResponse_WithNullString_ReturnsNull()
        {
            // Arrange
            var json = "null";
            
            // Act
            var result = KuzuResponseParser.ParseResponse(json, CodeContextJsonContext.Default, CodeContextJsonContext.Default.NodeDto);
            
            // Assert
            Assert.Null(result);
        }
        
        [Fact]
        public void ParseResponse_WithEmptyString_ReturnsNull()
        {
            // Arrange
            var json = "";
            
            // Act
            var result = KuzuResponseParser.ParseResponse(json, CodeContextJsonContext.Default, CodeContextJsonContext.Default.NodeDto);
            
            // Assert
            Assert.Null(result);
        }
        
        [Fact]
        public void ParseResponse_WithDirectObject_ParsesCorrectly()
        {
            // Arrange
            var json = @"{
                ""id"": ""test-1"",
                ""name"": ""TestClass"",
                ""type"": ""Class"",
                ""file_path"": ""/test.cs"",
                ""start_line"": 10,
                ""end_line"": 20
            }";
            
            // Act
            var result = KuzuResponseParser.ParseResponse(json, CodeContextJsonContext.Default, CodeContextJsonContext.Default.NodeDto);
            
            // Assert
            Assert.NotNull(result);
            Assert.Equal("test-1", result.Id);
            Assert.Equal("TestClass", result.Name);
            Assert.Equal("Class", result.Type);
        }
        
        [Fact]
        public void ParseResponse_WithObjectAndQueryStats_RemovesStatsAndParses()
        {
            // Arrange
            var json = @"{
                ""id"": ""test-1"",
                ""name"": ""TestClass"",
                ""type"": ""Class"",
                ""file_path"": ""/test.cs"",
                ""start_line"": 10,
                ""end_line"": 20,
                ""_query_stats"": {
                    ""query_time_ms"": 5,
                    ""cache_hit"": false
                }
            }";
            
            // Act
            var result = KuzuResponseParser.ParseResponse(json, CodeContextJsonContext.Default, CodeContextJsonContext.Default.NodeDto);
            
            // Assert
            Assert.NotNull(result);
            Assert.Equal("test-1", result.Id);
            Assert.Equal("TestClass", result.Name);
        }
        
        [Fact]
        public void ParseResponse_WithArrayInResults_ExtractsArray()
        {
            // Arrange
            var json = @"{
                ""results"": [
                    {""id"": ""1"", ""name"": ""Class1"", ""type"": ""Class"", ""file_path"": ""/a.cs"", ""start_line"": 1, ""end_line"": 10},
                    {""id"": ""2"", ""name"": ""Class2"", ""type"": ""Class"", ""file_path"": ""/b.cs"", ""start_line"": 1, ""end_line"": 20}
                ],
                ""_query_stats"": {
                    ""query_time_ms"": 10,
                    ""nodes_processed"": 2
                }
            }";
            
            // Act
            var result = KuzuResponseParser.ParseResponse(json, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListNodeDto);
            
            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Equal("Class1", result[0].Name);
            Assert.Equal("Class2", result[1].Name);
        }
        
        [Fact]
        public void ParseResponse_WithTimeoutError_ThrowsTimeoutException()
        {
            // Arrange
            var json = @"{
                ""error"": true,
                ""error_type"": ""query_timeout"",
                ""message"": ""Query exceeded 1.0 second timeout"",
                ""suggestions"": [""Use more specific filters"", ""Reduce traversal depth""]
            }";
            
            // Act & Assert
            var ex = Assert.Throws<TimeoutException>(() =>
                KuzuResponseParser.ParseResponse(json, CodeContextJsonContext.Default, CodeContextJsonContext.Default.NodeDto)
            );
            
            Assert.Contains("Query exceeded 1.0 second timeout", ex.Message);
            Assert.Contains("Use more specific filters", ex.Message);
        }
        
        [Fact]
        public void ParseResponse_WithQueryError_ThrowsInvalidOperationException()
        {
            // Arrange
            var json = @"{
                ""error"": true,
                ""error_type"": ""query_error"",
                ""message"": ""Invalid query syntax""
            }";
            
            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() =>
                KuzuResponseParser.ParseResponse(json, CodeContextJsonContext.Default, CodeContextJsonContext.Default.NodeDto)
            );
            
            Assert.Contains("Invalid query syntax", ex.Message);
        }
        
        [Fact]
        public void ParseResponse_WithFileMetadataCount_ParsesCorrectly()
        {
            // Arrange
            var json = @"{
                ""count"": 42,
                ""_query_stats"": {
                    ""query_time_ms"": 3
                }
            }";
            
            // Act
            var result = KuzuResponseParser.ParseResponse(json, CodeContextJsonContext.Default, CodeContextJsonContext.Default.DictionaryStringObject);
            
            // Assert
            Assert.NotNull(result);
            Assert.True(result.ContainsKey("count"));
            // The value in the dictionary is a JsonElement, need to get the actual value
            var countValue = result["count"];
            if (countValue is JsonElement jsonElement)
            {
                Assert.Equal(42, jsonElement.GetInt32());
            }
            else
            {
                Assert.Equal(42, Convert.ToInt32(countValue));
            }
        }
    }
}
