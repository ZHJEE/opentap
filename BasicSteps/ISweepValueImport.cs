//            Copyright Keysight Technologies 2012-2019
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, you can obtain one at http://mozilla.org/MPL/2.0/.
using OpenTap;
using System.Collections.Generic;

namespace OpenTap.Plugins.BasicSteps
{
    /// <summary>
    /// Custom handler for importing sweep parameters from a file.
    /// </summary>
    [Display("Sweep Value Import")]
    public interface ISweepValueImport : ITapPlugin
    {
        /// <summary>
        /// The extension of the imported file including the '.'. For example '.zip'.
        /// </summary>
        string Extension { get; }

        /// <summary>
        /// Name of the file format. Shown when the user selects the format in the GUI.
        /// For example, Compressed Using Zip.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Exports currently configured sweep values to a file.
        /// </summary>
        /// <param name="parameters">The currently configured sweep loop parameters.</param>
        /// <param name="parameterFilePath">Location of the file.</param>
        void ImportSweepValues(List<SweepParam> parameters, string parameterFilePath);
    }
}
