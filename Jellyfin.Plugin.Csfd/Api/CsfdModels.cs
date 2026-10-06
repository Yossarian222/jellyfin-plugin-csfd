using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>Detail filmu / seriálu / sezóny / epizódy z csfd-api (/movie/:id).</summary>
public sealed class CsfdMovie
{
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Id { get; set; }

    public string? Title { get; set; }

    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Year { get; set; }

    public string? Type { get; set; }

    public string? Url { get; set; }

    /// <summary>Hlavné ČSFD hodnotenie v percentách (to veľké číslo na stránke filmu).</summary>
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Rating { get; set; }

    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? RatingCount { get; set; }

    public string? Poster { get; set; }

    public string? Photo { get; set; }

    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Duration { get; set; }

    public List<CsfdTitleOther>? TitlesOther { get; set; }

    public List<string>? Origins { get; set; }

    public List<string>? Descriptions { get; set; }

    public List<string>? Genres { get; set; }

    public CsfdCreators? Creators { get; set; }

    public List<string>? Tags { get; set; }

    public List<CsfdPremiere>? Premieres { get; set; }

    public List<CsfdSeriesChild>? Seasons { get; set; }

    public List<CsfdSeriesChild>? Episodes { get; set; }

    public CsfdParent? Parent { get; set; }

    public string? EpisodeCode { get; set; }

    public string? SeasonName { get; set; }
}

public sealed class CsfdTitleOther
{
    public string? Country { get; set; }

    public string? Title { get; set; }
}

public sealed class CsfdPremiere
{
    public string? Country { get; set; }

    public string? Format { get; set; }

    /// <summary>Dátum vo formáte yyyy-MM-dd.</summary>
    public string? Date { get; set; }

    public string? Company { get; set; }
}

public sealed class CsfdPerson
{
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Id { get; set; }

    public string? Name { get; set; }

    public string? Url { get; set; }
}

public sealed class CsfdCreators
{
    public List<CsfdPerson>? Directors { get; set; }

    public List<CsfdPerson>? Writers { get; set; }

    public List<CsfdPerson>? Cinematography { get; set; }

    public List<CsfdPerson>? Music { get; set; }

    public List<CsfdPerson>? Actors { get; set; }

    public List<CsfdPerson>? Producers { get; set; }
}

public sealed class CsfdSeriesChild
{
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Id { get; set; }

    public string? Title { get; set; }

    public string? Url { get; set; }

    /// <summary>Pri sezóne rok, pri epizóde kód typu S01E01 / E01.</summary>
    public string? Info { get; set; }
}

public sealed class CsfdParentRef
{
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Id { get; set; }

    public string? Title { get; set; }
}

public sealed class CsfdParent
{
    public CsfdParentRef? Season { get; set; }

    public CsfdParentRef? Series { get; set; }
}

/// <summary>Výsledok vyhľadávania (/search/:query).</summary>
public sealed class CsfdSearchResult
{
    public List<CsfdSearchItem>? Movies { get; set; }

    public List<CsfdSearchItem>? TvSeries { get; set; }
}

public sealed class CsfdSearchItem
{
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Id { get; set; }

    public string? Title { get; set; }

    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? Year { get; set; }

    public string? Type { get; set; }

    public string? Url { get; set; }

    public string? Poster { get; set; }

    public List<string>? Origins { get; set; }
}

/// <summary>csfd-api vracia niektoré čísla raz ako číslo, raz ako reťazec ("2018", "120 min").</summary>
public sealed class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var i) ? i : (int)Math.Round(reader.GetDouble());
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return null;
                }

                var digits = new System.Text.StringBuilder();
                foreach (var c in s)
                {
                    if (char.IsDigit(c))
                    {
                        digits.Append(c);
                    }
                    else if (digits.Length > 0)
                    {
                        break;
                    }
                }

                return int.TryParse(digits.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
