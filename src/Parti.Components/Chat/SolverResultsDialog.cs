// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2026 Zohar Tito
// SolverResultsDialog — v0.8 Eto dialog that previews a FitnessFunction + its Phenotype population
// after a Galapagos/Octopus/Wallacei run. Scaffolded; not yet wired into ChatForm (see v0.9 note).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Parti.Core.Design;

namespace Parti.Components.Chat;

/// <summary>
/// Event payload raised when the user clicks "Apply this result to canvas" on a phenotype row.
/// The backend (v0.9) listens and writes the phenotype's <see cref="Phenotype.GenomeValues"/>
/// back to the live Grasshopper sliders. For v0.8 this is a pure notification stub.
/// </summary>
/// <param name="Phenotype">The phenotype the user selected.</param>
public sealed record PhenotypeApplyRequestedEventArgs(Phenotype Phenotype);

/// <summary>
/// Modal dialog that displays the result of a solver run. Shows two tabs:
/// <list type="bullet">
///   <item><b>Fitness</b> — the <see cref="FitnessFunction"/> that was submitted: chosen solver,
///     clauses (metric · direction · target · weight), and genomes (slider ∈ [min..max] step).</item>
///   <item><b>Results</b> — the resulting <see cref="Phenotype"/> population, one row per instance,
///     with rank, metric scores, genome values, optional viewport thumbnail, and an "apply" button.</item>
/// </list>
/// <para>
/// v0.8 status: scaffolded but <b>not yet wired into <see cref="ChatForm"/></b>. The plan preview
/// flow in v0.9 will open this dialog after a solver run completes. Callers subscribe to
/// <see cref="ApproveAndStartSolverClicked"/> and <see cref="PhenotypeApplyRequested"/> to react
/// to user actions. See TODO v0.9 in <see cref="ChatForm"/> for wiring follow-up.
/// </para>
/// </summary>
public sealed class SolverResultsDialog : Dialog
{
    private readonly FitnessFunction _fitness;
    private readonly IReadOnlyList<Phenotype> _results;

    /// <summary>Raised when the user clicks "Approve + Start Solver" on the Fitness tab.</summary>
    public event EventHandler? ApproveAndStartSolverClicked;

    /// <summary>Raised when the user clicks "Apply this result to canvas" on a phenotype row.</summary>
    public event EventHandler<PhenotypeApplyRequestedEventArgs>? PhenotypeApplyRequested;

    /// <summary>
    /// Creates the dialog. Tabs appear conditionally: the Fitness tab is always shown (required),
    /// and the Results tab is shown only when <paramref name="results"/> is non-empty.
    /// </summary>
    /// <param name="fitness">The fitness function submitted to the solver. Required.</param>
    /// <param name="results">The phenotype population returned by the solver. May be empty.</param>
    public SolverResultsDialog(FitnessFunction fitness, IReadOnlyList<Phenotype>? results = null)
    {
        _fitness = fitness ?? throw new ArgumentNullException(nameof(fitness));
        _results = results ?? Array.Empty<Phenotype>();

        Title = "Parti \u2014 Solver results";
        MinimumSize = new Size(560, 420);
        Padding = new Padding(10);
        Resizable = true;

        var tabs = new TabControl();
        tabs.Pages.Add(new TabPage { Text = "Fitness", Content = BuildFitnessTab() });

        if (_results.Count > 0)
        {
            tabs.Pages.Add(new TabPage { Text = "Results", Content = BuildResultsTab() });
        }

        var closeButton = new Button { Text = "Close" };
        closeButton.Click += (_, _) => Close();
        AbortButton = closeButton;

        Content = new TableLayout
        {
            Spacing = new Size(6, 6),
            Rows =
            {
                new TableRow(new TableCell(tabs, true)) { ScaleHeight = true },
                new TableRow(new TableCell(
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        HorizontalContentAlignment = HorizontalAlignment.Right,
                        Items = { new StackLayoutItem(closeButton) },
                    },
                    true)),
            },
        };
    }

    private Control BuildFitnessTab()
    {
        var solverLabel = new Label
        {
            Text = $"Solver: {_fitness.ChosenSolver}",
            Font = new Font(SystemFont.Bold, 11),
        };

        var clausesHeader = new Label { Text = "Clauses", Font = new Font(SystemFont.Bold, 10) };
        var clausesText = new TextArea
        {
            ReadOnly = true,
            Wrap = false,
            Font = new Font(FontFamilies.Monospace, 10),
            Text = BuildClausesText(_fitness.Clauses),
        };

        var genomesHeader = new Label { Text = "Genomes", Font = new Font(SystemFont.Bold, 10) };
        var genomesText = new TextArea
        {
            ReadOnly = true,
            Wrap = false,
            Font = new Font(FontFamilies.Monospace, 10),
            Text = BuildGenomesText(_fitness.Genomes),
        };

        var approveButton = new Button { Text = "Approve + Start Solver" };
        approveButton.Click += (s, e) => ApproveAndStartSolverClicked?.Invoke(this, EventArgs.Empty);

        return new TableLayout
        {
            Spacing = new Size(6, 6),
            Padding = new Padding(8),
            Rows =
            {
                new TableRow(new TableCell(solverLabel, true)),
                new TableRow(new TableCell(clausesHeader, true)),
                new TableRow(new TableCell(clausesText, true)) { ScaleHeight = true },
                new TableRow(new TableCell(genomesHeader, true)),
                new TableRow(new TableCell(genomesText, true)) { ScaleHeight = true },
                new TableRow(new TableCell(
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalContentAlignment = HorizontalAlignment.Right,
                        Items = { new StackLayoutItem(approveButton) },
                    },
                    true)),
            },
        };
    }

    private static string BuildClausesText(IReadOnlyList<FitnessClause> clauses)
    {
        if (clauses is null || clauses.Count == 0)
        {
            return "(no clauses)";
        }

        var lines = clauses.Select(c =>
        {
            string target = c.Target.HasValue ? c.Target.Value.ToString("G") : "\u2014";
            return $"{c.Metric}  \u00b7  {c.Direction}  \u00b7  target={target}  \u00b7  weight={c.Weight:G}";
        });
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildGenomesText(IReadOnlyList<Genome> genomes)
    {
        if (genomes is null || genomes.Count == 0)
        {
            return "(no genomes)";
        }

        var lines = genomes.Select(g =>
        {
            string step = g.Step.HasValue ? $", step={g.Step.Value:G}" : "";
            return $"{g.SliderName} \u2208 [{g.Min:G} .. {g.Max:G}]{step}";
        });
        return string.Join(Environment.NewLine, lines);
    }

    private Control BuildResultsTab()
    {
        var stack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Padding = new Padding(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        foreach (var phenotype in _results)
        {
            stack.Items.Add(new StackLayoutItem(BuildPhenotypeRow(phenotype), HorizontalAlignment.Stretch));
        }

        return new Scrollable { Content = stack, Border = BorderType.None };
    }

    private Control BuildPhenotypeRow(Phenotype phenotype)
    {
        var rankLabel = new Label
        {
            Text = $"Rank {phenotype.Rank}",
            Font = new Font(SystemFont.Bold, 11),
        };

        var metricsText = new Label
        {
            Text = "Metrics: " + FormatDict(phenotype.MetricValues),
            Wrap = WrapMode.Word,
        };

        var genomesText = new Label
        {
            Text = "Genomes: " + FormatDict(phenotype.GenomeValues),
            Wrap = WrapMode.Word,
        };

        var applyButton = new Button { Text = "Apply this result to canvas" };
        applyButton.Click += (s, e) =>
            PhenotypeApplyRequested?.Invoke(this, new PhenotypeApplyRequestedEventArgs(phenotype));

        var textColumn = new TableLayout
        {
            Spacing = new Size(4, 4),
            Rows =
            {
                new TableRow(new TableCell(rankLabel, true)),
                new TableRow(new TableCell(metricsText, true)),
                new TableRow(new TableCell(genomesText, true)),
                new TableRow(new TableCell(applyButton, true)),
            },
        };

        // Optional thumbnail: render only when the path is set AND the file exists.
        // Bitmap ctor throws on missing file, so guard defensively — headless/pre-capture is legal.
        Control? thumb = TryLoadThumbnail(phenotype.ViewportScreenshotPath);

        if (thumb is null)
        {
            return new Panel
            {
                Padding = new Padding(6),
                Content = textColumn,
            };
        }

        var rowLayout = new TableLayout
        {
            Spacing = new Size(8, 0),
            Rows =
            {
                new TableRow(
                    new TableCell(thumb, false),
                    new TableCell(textColumn, true)),
            },
        };

        return new Panel { Padding = new Padding(6), Content = rowLayout };
    }

    private static Control? TryLoadThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new Bitmap(path);
            return new ImageView
            {
                Image = bitmap,
                Size = new Size(96, 72),
            };
        }
        catch
        {
            // Unreadable / unsupported format — skip gracefully per task spec.
            return null;
        }
    }

    private static string FormatDict(IReadOnlyDictionary<string, double> dict)
    {
        if (dict is null || dict.Count == 0)
        {
            return "(none)";
        }
        return string.Join(", ", dict.Select(kv => $"{kv.Key}={kv.Value:G}"));
    }
}
