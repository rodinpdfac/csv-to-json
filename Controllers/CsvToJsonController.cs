using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CsvHelper;
using CsvHelper.Configuration;
using System.IO;


namespace CsvToJsonCore.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class CsvToJsonController : ControllerBase
    {

        private readonly ILogger<CsvToJsonController> _logger;

        public CsvToJsonController(ILogger<CsvToJsonController> logger)
        {
            _logger = logger;
        }

        [HttpPost]
        public ActionResult Post([FromBody] string body, [FromQuery] char delimiter = ';')
        {
            if (string.IsNullOrEmpty(body))
            {
                return BadRequest(new { error = "Corpo da requisição (CSV) está vazio ou ausente. Verifique se o Logic App envia o conteúdo do arquivo como texto no body (ex.: @body('Get_file_content_using_path') ou @body('Get_file_content_using_path')?['$content'])." });
            }

            try
            {
                // Detecta o delimitador pela primeira linha se não ficar claro (evita erro quando Logic App não envia ?delimiter=;)
                char detectedDelimiter = DetectDelimiterFromFirstLine(body);
                if (detectedDelimiter != '\0')
                    delimiter = detectedDelimiter;

                return Ok(ConvertCsvToJson(body, delimiter));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao converter CSV para JSON. Delimiter={Delimiter}", delimiter);
                return StatusCode(500, new { error = "Erro interno ao processar CSV.", detail = ex.Message });
            }
        }

        /// <summary>
        /// Converte o conteúdo CSV em um objeto JSON (lista de linhas com chaves do cabeçalho).
        /// Se o delimitador informado não for vírgula, o corpo é normalizado (delimitador substituído por vírgula,
        /// respeitando campos entre aspas duplas) e o parser usa sempre vírgula.
        /// </summary>
        /// <param name="body">Conteúdo bruto do CSV (texto).</param>
        /// <param name="delimiter">Caractere delimitador de colunas no CSV de entrada (ex.: ';' ou ',').</param>
        /// <returns>Objeto com propriedade 'rows' contendo um array de dicionários por linha.</returns>
        private static JsonResult ConvertCsvToJson(string body, char delimiter)
        {
            // Normaliza delimitador para vírgula: substitui o delimitador de entrada por ',' (respeitando aspas)
            // para que o parser use sempre vírgula internamente.
            if (delimiter != ',')
            {
                body = NormalizeDelimiterToComma(body, delimiter);
                delimiter = ',';
            }

            JsonResult resultSet = new JsonResult();
            string value;
            string[] headers = new string[1024];

            using (TextReader sr = new StringReader(body))
            {
                var config = new CsvConfiguration(System.Globalization.CultureInfo.CurrentCulture) { Delimiter = delimiter.ToString() };
                using var csv = new CsvReader(sr, config);

                //read header - not necessary to leverage header record functionality currently
                //csv.Configuration.HasHeaderRecord = false;
                if (csv.Read())
                {
                    for (int i = 0; csv.TryGetField<string>(i, out value); i++)
                    {
                        headers[i] = value;
                    }
                }

                // Conta quantas colunas o cabeçalho tem (até o primeiro null)
                int headerCount = 0;
                while (headerCount < headers.Length && headers[headerCount] != null)
                    headerCount++;

                //read the rest of the file
                while (csv.Read())
                {
                    //initialize a new row object
                    var rowObject = new Dictionary<string, string>();
                    int fieldIndex = 0;

                    //loop through each element in the row
                    for (int i = 0; csv.TryGetField<string>(i, out value); i++)
                    {
                        if (i >= headerCount)
                        {
                            throw new InvalidOperationException(
                                "Número de colunas na linha de dados maior que o cabeçalho. Verifique se o delimitador da requisição (?delimiter=) corresponde ao usado no CSV (ex.: ; ou ,).");
                        }
                        // Evita exceção por cabeçalho duplicado: usa chave única se já existir
                        string key = headers[i];
                        if (rowObject.ContainsKey(key))
                        {
                            int suffix = 1;
                            while (rowObject.ContainsKey(key + "_" + suffix)) suffix++;
                            key = key + "_" + suffix;
                        }
                        rowObject.Add(key, value);
                        fieldIndex = i + 1;
                    }

                    if (fieldIndex < headerCount)
                    {
                        throw new InvalidOperationException(
                            "Número de colunas na linha de dados menor que o cabeçalho. Verifique se o delimitador da requisição (?delimiter=) corresponde ao usado no CSV (ex.: ; ou ,).");
                    }
                    //Add the populated row object to the row array
                    resultSet.rows.Add(rowObject);
                    
                }
            }
            return resultSet;
        }

        /// <summary>
        /// Substitui o delimitador por vírgula no texto CSV, respeitando campos entre aspas duplas.
        /// Delimitadores dentro de "..." não são alterados.
        /// </summary>
        /// <param name="csvContent">Conteúdo bruto do CSV.</param>
        /// <param name="delimiter">Caractere delimitador a ser substituído (ex.: ';').</param>
        /// <returns>Texto com o delimitador substituído por ',' fora de aspas.</returns>
        /// <summary>
        /// Detecta o delimitador de colunas pela primeira linha do CSV (cabeçalho).
        /// Conta vírgulas e ponto e vírgula fora de aspas; retorna o que aparecer mais.
        /// </summary>
        /// <param name="csvContent">Conteúdo bruto do CSV.</param>
        /// <returns>';' ou ',' conforme a primeira linha, ou '\0' se não for possível detectar.</returns>
        private static char DetectDelimiterFromFirstLine(string csvContent)
        {
            if (string.IsNullOrEmpty(csvContent)) return '\0';

            int firstLineEnd = csvContent.IndexOfAny(new[] { '\r', '\n' });
            string firstLine = firstLineEnd >= 0 ? csvContent.Substring(0, firstLineEnd) : csvContent;
            if (string.IsNullOrWhiteSpace(firstLine)) return '\0';

            int commas = 0, semicolons = 0;
            bool insideQuotes = false;

            for (int i = 0; i < firstLine.Length; i++)
            {
                char c = firstLine[i];
                if (c == '"')
                {
                    if (i + 1 < firstLine.Length && firstLine[i + 1] == '"')
                        i++;
                    else
                        insideQuotes = !insideQuotes;
                }
                else if (!insideQuotes)
                {
                    if (c == ',') commas++;
                    else if (c == ';') semicolons++;
                }
            }

            if (semicolons > commas) return ';';
            if (commas > semicolons) return ',';
            return '\0';
        }

        private static string NormalizeDelimiterToComma(string csvContent, char delimiter)
        {
            if (string.IsNullOrEmpty(csvContent) || delimiter == ',') return csvContent;

            var result = new System.Text.StringBuilder(csvContent.Length);
            bool insideQuotes = false;

            for (int i = 0; i < csvContent.Length; i++)
            {
                char c = csvContent[i];

                if (c == '"')
                {
                    // Aspas escapadas ("") dentro de campo: mantém e não alterna insideQuotes
                    if (insideQuotes && i + 1 < csvContent.Length && csvContent[i + 1] == '"')
                    {
                        result.Append('"').Append('"');
                        i++;
                    }
                    else
                    {
                        insideQuotes = !insideQuotes;
                        result.Append(c);
                    }
                }
                else if (c == delimiter && !insideQuotes)
                {
                    result.Append(',');
                }
                else
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }
    }
}
