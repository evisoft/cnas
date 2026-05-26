using System.Collections.Generic;
using Cnas.Ps.Application.Classifiers;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Persistence.Seed;

/// <summary>
/// R0401 / TOR CF 17.02-04 — factory that emits the minimal stub rows for the
/// five official national classifier schemes mirrored by SI PS. The full
/// national sets are pulled from external feeds via R0100 / MConnect; this
/// helper just seeds a representative starter sample so the
/// <c>Classifier.SchemeCode</c> discriminator is exercised, the
/// <c>IClassifierService.UpsertAsync / DeactivateAsync</c> read-only-mirror
/// gate has rows to exercise, and the Blazor classifier picker can render a
/// non-empty dropdown out-of-the-box.
/// </summary>
/// <remarks>
/// <para>
/// Every row produced by this factory carries <see cref="Classifier.IsReadOnlyMirror"/>
/// set to <c>true</c> + <see cref="Classifier.Source"/> set to <c>"national"</c>.
/// Mutating one through <c>IClassifierService</c> short-circuits with the
/// stable error code <see cref="Cnas.Ps.Core.Common.ErrorCodes.ClassifierReadonlyMirror"/>.
/// </para>
/// <para>
/// The factory is deterministic — every call returns rows with identical
/// codes, labels, and parent-code wiring — so integration tests can seed it
/// once and assert against the exact (kind, code) set.
/// </para>
/// </remarks>
public static class NationalClassifiersSeed
{
    /// <summary>
    /// Returns every national classifier row this build seeds. The collection
    /// is materialised so callers can enumerate it twice (Add + assert).
    /// </summary>
    /// <param name="createdAtUtc">UTC timestamp stamped on every seeded row.</param>
    /// <returns>
    /// A read-only list of <see cref="Classifier"/> rows ready to be added to
    /// <c>DbContext.Classifiers</c>.
    /// </returns>
    public static IReadOnlyList<Classifier> BuildAll(System.DateTime createdAtUtc)
    {
        var rows = new List<Classifier>();
        rows.AddRange(BuildCaemSample(createdAtUtc));
        rows.AddRange(BuildCuatmSample(createdAtUtc));
        rows.AddRange(BuildCfojSample(createdAtUtc));
        rows.AddRange(BuildCfpSample(createdAtUtc));
        rows.AddRange(BuildNcmSample(createdAtUtc));
        return rows;
    }

    /// <summary>CAEM Rev.2 starter sample — 10 two-digit divisions across the alphabet.</summary>
    /// <param name="createdAtUtc">Audit timestamp.</param>
    /// <returns>The CAEM Rev.2 sample slice.</returns>
    public static IReadOnlyList<Classifier> BuildCaemSample(System.DateTime createdAtUtc)
    {
        // Section A (Agriculture) through Section S (Other services). Two-digit
        // divisions chosen so each top-level section has a representative entry.
        return
        [
            NewNational(ClassifierSchemeFamilies.Caem, "01", "Cultivare plante și creșterea animalelor",
                "Crop and animal production", "Растениеводство и животноводство", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Caem, "10", "Industria alimentară",
                "Manufacture of food products", "Производство пищевых продуктов", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Caem, "35", "Producția și furnizarea de energie",
                "Electricity, gas, steam and air conditioning supply",
                "Производство, передача и распределение электроэнергии", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Caem, "41", "Construcții de clădiri",
                "Construction of buildings", "Строительство зданий", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Caem, "47", "Comerț cu amănuntul",
                "Retail trade", "Розничная торговля", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Caem, "49", "Transporturi terestre",
                "Land transport and transport via pipelines",
                "Сухопутный и трубопроводный транспорт", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Caem, "62", "Activități IT",
                "Computer programming and consultancy",
                "Деятельность в области информационных технологий", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Caem, "64", "Servicii financiare",
                "Financial service activities", "Финансовые услуги", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Caem, "85", "Învățământ",
                "Education", "Образование", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Caem, "86", "Activități referitoare la sănătate umană",
                "Human health activities", "Здравоохранение", createdAtUtc),
        ];
    }

    /// <summary>CUATM starter sample — 6 representative raions.</summary>
    /// <param name="createdAtUtc">Audit timestamp.</param>
    /// <returns>The CUATM sample slice.</returns>
    public static IReadOnlyList<Classifier> BuildCuatmSample(System.DateTime createdAtUtc)
    {
        return
        [
            NewNational(ClassifierSchemeFamilies.Cuatm, "0100", "Municipiul Chișinău",
                "Chișinău municipality", "Муниципий Кишинёв", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cuatm, "1100", "Raionul Anenii Noi",
                "Anenii Noi district", "Анений-Ной", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cuatm, "1500", "Raionul Bălți (municipiu)",
                "Bălți municipality", "Бельцы", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cuatm, "2300", "Raionul Cahul",
                "Cahul district", "Кагул", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cuatm, "5500", "Raionul Orhei",
                "Orhei district", "Оргеев", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cuatm, "9100", "UTA Găgăuzia",
                "Gagauzia autonomous unit", "АТО Гагаузия", createdAtUtc),
        ];
    }

    /// <summary>CFOJ starter sample — 8 representative occupation codes.</summary>
    /// <param name="createdAtUtc">Audit timestamp.</param>
    /// <returns>The CFOJ sample slice.</returns>
    public static IReadOnlyList<Classifier> BuildCfojSample(System.DateTime createdAtUtc)
    {
        return
        [
            NewNational(ClassifierSchemeFamilies.Cfoj, "1120", "Manager general",
                "Chief executive", "Генеральный директор", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfoj, "2310", "Cadre didactice universitare",
                "University academic staff", "Преподаватели вузов", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfoj, "2330", "Profesori în învățământ liceal",
                "Secondary education teachers", "Преподаватели средней школы", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfoj, "2412", "Specialiști financiari",
                "Financial and investment advisers", "Финансовые консультанты", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfoj, "2511", "Analiști sisteme IT",
                "Systems analysts", "Системные аналитики", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfoj, "2512", "Programatori",
                "Software developers", "Программисты", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfoj, "3211", "Tehnicieni imagistică medicală",
                "Medical imaging technicians", "Техники медицинской визуализации", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfoj, "5120", "Bucătari",
                "Cooks", "Повара", createdAtUtc),
        ];
    }

    /// <summary>CFP starter sample — 8 legal-form codes from Legea 220/2007 + Codul Civil.</summary>
    /// <param name="createdAtUtc">Audit timestamp.</param>
    /// <returns>The CFP sample slice.</returns>
    public static IReadOnlyList<Classifier> BuildCfpSample(System.DateTime createdAtUtc)
    {
        return
        [
            NewNational(ClassifierSchemeFamilies.Cfp, "SRL", "Societate cu răspundere limitată",
                "Limited liability company", "ООО", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfp, "SA", "Societate pe acțiuni",
                "Joint-stock company", "АО", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfp, "II", "Întreprindere individuală",
                "Sole proprietorship", "Индивидуальное предприятие", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfp, "GT", "Gospodărie țărănească",
                "Peasant household", "Крестьянское хозяйство", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfp, "AO", "Asociație obștească",
                "Public association", "Общественное объединение", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfp, "FUN", "Fundație",
                "Foundation", "Фонд", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfp, "COOP", "Cooperativă",
                "Cooperative", "Кооператив", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Cfp, "IS", "Întreprindere de stat",
                "State-owned enterprise", "Государственное предприятие", createdAtUtc),
        ];
    }

    /// <summary>NCM starter sample — six common currencies.</summary>
    /// <param name="createdAtUtc">Audit timestamp.</param>
    /// <returns>The NCM sample slice.</returns>
    public static IReadOnlyList<Classifier> BuildNcmSample(System.DateTime createdAtUtc)
    {
        return
        [
            NewNational(ClassifierSchemeFamilies.Ncm, "MDL", "Leu moldovenesc",
                "Moldovan leu", "Молдавский лей", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Ncm, "EUR", "Euro",
                "Euro", "Евро", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Ncm, "USD", "Dolar SUA",
                "US dollar", "Доллар США", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Ncm, "RON", "Leu românesc",
                "Romanian leu", "Румынский лей", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Ncm, "UAH", "Grivnă ucraineană",
                "Ukrainian hryvnia", "Украинская гривна", createdAtUtc),
            NewNational(ClassifierSchemeFamilies.Ncm, "RUB", "Rublă rusă",
                "Russian ruble", "Российский рубль", createdAtUtc),
        ];
    }

    /// <summary>
    /// Factory helper. Stamps <see cref="Classifier.IsReadOnlyMirror"/> to
    /// <c>true</c> + <see cref="Classifier.Source"/> to <c>"national"</c> on
    /// every emitted row.
    /// </summary>
    /// <param name="kind">Scheme code (e.g. <see cref="ClassifierSchemeFamilies.Caem"/>).</param>
    /// <param name="code">Stable code value within the scheme.</param>
    /// <param name="labelRo">Romanian label.</param>
    /// <param name="labelEn">English label.</param>
    /// <param name="labelRu">Russian label.</param>
    /// <param name="createdAtUtc">Audit timestamp.</param>
    /// <returns>A populated <see cref="Classifier"/> row.</returns>
    private static Classifier NewNational(
        string kind,
        string code,
        string labelRo,
        string labelEn,
        string labelRu,
        System.DateTime createdAtUtc) =>
        new()
        {
            CreatedAtUtc = createdAtUtc,
            Kind = kind,
            Code = code,
            LabelRo = labelRo,
            LabelEn = labelEn,
            LabelRu = labelRu,
            Source = "national",
            IsActive = true,
            IsReadOnlyMirror = true,
        };
}
