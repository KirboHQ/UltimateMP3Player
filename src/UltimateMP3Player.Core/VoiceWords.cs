using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

public enum VoiceFocus { Any, Song, Artist, Album, Playlist, Genre, Station }
public enum VoiceMeaning { Name, AnyMusic, Shuffle, Favorites }

// What spoken words ask for (the app's VoicePlay plays it). The assistants hand over only the name of what to play, in any language;
// the few words that mean something of their own (music, shuffle, favorites, "the album…", "songs by…", "the radio…") are known in
// English, Italian, Spanish, French, German and Portuguese, with their usual other ways of saying them. A Shortcut's dictation
// brings the verb and the app's name too ("play X on UMP"): left out. `mp3cli voice` checks it.
public static class VoiceWords
{
    private static readonly HashSet<string> AnyMusic = Keys(
        "music", "some music", "my music", "songs", "my songs", "something",
        "musica", "la mia musica", "un po di musica", "canzoni", "le mie canzoni", "brani", "i miei brani", "qualcosa",
        "mi musica", "algo de musica", "canciones", "mis canciones", "algo",
        "musique", "de la musique", "ma musique", "chansons", "mes chansons", "quelque chose",
        "musik", "meine musik", "etwas musik", "lieder", "meine lieder", "etwas",
        "minha musica", "minhas musicas", "cancoes", "minhas cancoes", "alguma coisa");
    private static readonly HashSet<string> ShuffleWords = Keys(
        "shuffle", "shuffle all", "on shuffle", "shuffled", "random", "everything", "my library",
        "casuale", "in ordine casuale", "riproduzione casuale", "musica casuale", "brani casuali", "canzoni a caso", "a caso", "in modalita casuale", "tutto",
        "la mia libreria", "tutta la libreria",
        "aleatorio", "modo aleatorio", "musica aleatoria", "al azar", "canciones al azar", "todo", "mi biblioteca",
        "aleatoire", "lecture aleatoire", "musique aleatoire", "au hasard", "tout", "ma bibliotheque",
        "zufall", "zufallswiedergabe", "zufallig", "alles", "meine bibliothek", "meine mediathek",
        "musicas aleatorias", "tudo", "minha biblioteca");
    private static readonly HashSet<string> FavoriteWords = Keys(
        "favorites", "favourites", "my favorites", "my favourites", "liked songs", "my liked songs", "songs i like", "loved songs", "likes",
        "preferiti", "i preferiti", "i miei preferiti", "brani preferiti", "canzoni preferite", "mi piace", "brani che mi piacciono",
        "canzoni che mi piacciono", "le mie preferite",
        "favoritos", "mis favoritos", "me gusta", "canciones que me gustan", "canciones favoritas",
        "favoris", "mes favoris", "titres likes", "titres aimes", "chansons preferees", "coups de coeur",
        "favoriten", "meine favoriten", "lieblingssongs", "lieblingslieder", "gefallt mir",
        "meus favoritos", "musicas curtidas", "curtidas", "musicas favoritas");

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Verb = new(@"^\s*(please\s+|per\s+favore\s+|por\s+favor\s+|bitte\s+)?(play|put\s+on|listen\s+to|start|"
        + @"riproduci|metti(\s+su)?|suona|ascolta|fai\s+partire|fammi\s+sentire|fammi\s+ascoltare|"
        + @"reproduce|reproducir|pon(me)?|escucha(r)?|toca(r)?|"
        + @"joue(r)?|mets|mettre|[ée]coute(r)?|lance(r)?|"
        + @"spiele?|abspielen|h[öo]re(n)?|"
        + @"toque|reproduz(a|ir)?|ouvir|ou[çc]a|coloca(r)?|bota(r)?)\s+", Options);
    private static readonly Regex AppName = new(@"\s+(on|su|con|in|en|sur|avec|auf|mit|no|na|em|com)\s+(ultimate(\s*mp3(\s*player)?)?|ump|mp3\s*player)\s*[.!]?\s*$", Options);
    // "The album…", "songs by…", "the playlist…", "radio…", "genre…": what the rest is.
    private static readonly (Regex Words, VoiceFocus Focus)[] Kinds =
    {
        (Kind(@"(the\s+|my\s+|la\s+(mia\s+)?|mia\s+|mi\s+|ma\s+|mon\s+|meine\s+|die\s+|a\s+|minha\s+)?(playlist|lista(\s+de\s+reproducci[oó]n)?|liste)"), VoiceFocus.Playlist),
        (Kind(@"(the\s+|l'|l\s+|el\s+|le\s+|das\s+|o\s+)?([aá]lbum|disco)"), VoiceFocus.Album),
        (Kind(@"(songs|music|tracks|something)\s+(by|from)|(canzoni|brani|musica|pezzi|qualcosa)\s+(di|dei|degli|delle|del|della)|(canciones|m[uú]sica|temas|algo)\s+de(l)?|"
              + @"(chansons|musique|titres)\s+(de|des|du)|(lieder|musik|songs|titel)\s+(von|der)|(m[uú]sicas|can[cç][oõ]es)\s+(de|do|da|dos|das)|artist|artista|artiste|k[üu]nstler"),
            VoiceFocus.Artist),
        (Kind(@"(the\s+)?(radio(\s+station)?|stazione(\s+radio)?|la\s+radio|emisora|station|sender|r[aá]dio)"), VoiceFocus.Station),
        (Kind(@"(the\s+)?(genre|genere|g[eé]nero|tag)"), VoiceFocus.Genre),
    };
    private static readonly Regex Article = new(@"^(the|il|lo|la|i|gli|le|l'|el|los|las|les|der|die|das|o|os|as|a)\s+", Options);

    // "shuffle", or music said with it on either side: "musica a caso", "random music", "musique au hasard".
    private static bool Shuffled(string key)
    {
        if (ShuffleWords.Contains(key)) return true;
        var words = key.Split(' ');
        for (int i = 1; i < words.Length; i++)
        {
            string left = string.Join(' ', words[..i]), right = string.Join(' ', words[i..]);
            if ((AnyMusic.Contains(left) && ShuffleWords.Contains(right)) || (ShuffleWords.Contains(left) && AnyMusic.Contains(right))) return true;
        }
        return false;
    }

    private static Regex Kind(string words) => new(@"^\s*(" + words + @")\s+", Options);
    private static HashSet<string> Keys(params string[] words) => words.Select(Text.Normalize).ToHashSet();

    // The words without the verb and the app's name around them.
    public static string Clean(string words) => AppName.Replace(Verb.Replace(words.Trim(), ""), "").Trim().Trim('"', '“', '”', '«', '»');

    // What the words alone mean: any music, everything shuffled, the favorites, or a name (with what kind of name, when said).
    public static (VoiceMeaning Meaning, VoiceFocus Focus, string Name) Read(string words)
    {
        var name = Clean(words);
        var key = Text.Normalize(name);
        if (key.Length == 0 || AnyMusic.Contains(key)) return (VoiceMeaning.AnyMusic, VoiceFocus.Any, "");
        if (Shuffled(key)) return (VoiceMeaning.Shuffle, VoiceFocus.Any, "");
        if (FavoriteWords.Contains(key)) return (VoiceMeaning.Favorites, VoiceFocus.Playlist, name);
        foreach (var (kind, focus) in Kinds)
            if (kind.Match(name) is { Success: true } m && name[m.Length..].Trim() is { Length: > 0 } rest)
                return (VoiceMeaning.Name, focus, rest);
        return (VoiceMeaning.Name, VoiceFocus.Any, name);
    }

    // A name, then without the article before it ("i Queen", "the Beatles").
    public static IEnumerable<string> Names(string name)
    {
        if (name.Length == 0) yield break;
        yield return name;
        var bare = Article.Replace(name, "").Trim();
        if (bare.Length > 0 && bare != name) yield return bare;
    }
}
