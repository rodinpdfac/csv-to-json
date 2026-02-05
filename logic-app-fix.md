# Correção: delimitador na chamada da API no Logic App

## Onde está a chamada

No JSON do Logic App, a chamada à API CSV-to-JSON está na ação **`Convert_CSV_to_JSON`**, dentro de `definition.actions`.

## O que alterar

Troque a linha do `uri` de:

```json
"uri": "https://webapp-ogxeua37zxnqo.azurewebsites.net/csvtojson",
```

para:

```json
"uri": "https://webapp-ogxeua37zxnqo.azurewebsites.net/csvtojson?delimiter=;",
```

Assim a API passa a receber explicitamente o delimitador **ponto e vírgula**, igual ao CSV em `/apiprovisioningdata/ProvisioningUsers.csv`.

## Como aplicar no Portal do Azure

1. Abra o Logic App no Portal do Azure.
2. Vá em **Logic app designer** (ou **Designer**).
3. Clique na ação **Convert_CSV_to_JSON** (a que chama a Web App).
4. No campo **URI**, altere de:
   - `https://webapp-ogxeua37zxnqo.azurewebsites.net/csvtojson`
   para:
   - `https://webapp-ogxeua37zxnqo.azurewebsites.net/csvtojson?delimiter=;`
5. Salve o Logic App.

Se o designer tiver um campo separado para **Query** ou **Parâmetros**, use:
- Nome: `delimiter`
- Valor: `;`

e a URI pode continuar só: `https://webapp-ogxeua37zxnqo.azurewebsites.net/csvtojson`.
