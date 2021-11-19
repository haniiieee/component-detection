using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using Microsoft.ComponentDetection.Common;
using Microsoft.ComponentDetection.Common.DependencyGraph;
using Microsoft.ComponentDetection.Contracts;
using Microsoft.ComponentDetection.Contracts.BcdeModels;
using Microsoft.ComponentDetection.Contracts.TypedComponent;
using Microsoft.ComponentDetection.Orchestrator.ArgumentSets;

namespace Microsoft.ComponentDetection.Orchestrator.Services.GraphTranslation
{
    [Export(typeof(IGraphTranslationService))]
    [ExportMetadata("Priority", 0)]
    public class DefaultGraphTranslationService : ServiceBase, IGraphTranslationService
    {
        public ScanResult GenerateScanResultFromProcessingResult(DetectorProcessingResult detectorProcessingResult, IDetectionArguments detectionArguments)
        {
            var recorderDetectorPairs = detectorProcessingResult.ComponentRecorders;

            var unmergedComponents = GatherSetOfDetectedComponentsUnmerged(recorderDetectorPairs, detectionArguments.SourceDirectory);

            var mergedComponents = FlattenAndMergeComponents(unmergedComponents);

            return new DefaultGraphScanResult
            {
                ComponentsFound = mergedComponents.Select(x => ConvertToContract(x)).ToList(),
                ContainerDetailsMap = detectorProcessingResult.ContainersDetailsMap,
                DependencyGraphs = GraphTranslationUtility.AccumulateAndConvertToContract(recorderDetectorPairs
                                                                    .Select(tuple => tuple.recorder)
                                                                    .Where(x => x != null)
                                                                    .Select(x => x.GetDependencyGraphsByLocation())),
            };
        }

        private IEnumerable<DetectedComponent> GatherSetOfDetectedComponentsUnmerged(IEnumerable<(IComponentDetector detector, ComponentRecorder recorder)> recorderDetectorPairs, DirectoryInfo rootDirectory)
        {
            return recorderDetectorPairs
                .Where(recorderDetectorPair => recorderDetectorPair.recorder != null)
                .SelectMany(recorderDetectorPair =>
            {
                var detector = recorderDetectorPair.detector;
                var componentRecorder = recorderDetectorPair.recorder;
                var detectedComponents = componentRecorder.GetDetectedComponents();
                var dependencyGraphsByLocation = componentRecorder.GetDependencyGraphsByLocation();

                // Note that it looks like we are building up detected components functionally, but they are not immutable -- the code is just written 
                //  to look like a pipeline.
                foreach (var component in detectedComponents)
                {
                    // Reinitialize properties that might still be getting populated in ways we don't want to support, because the data is authoritatively stored in the graph.
                    component.FilePaths?.Clear();

                    // Information about each component is relative to all of the graphs it is present in, so we take all graphs containing a given component and apply the graph data.
                    foreach (var graphKvp in dependencyGraphsByLocation.Where(x => x.Value.Contains(component.Component.Id)))
                    {
                        var location = graphKvp.Key;
                        var dependencyGraph = graphKvp.Value;

                        // Calculate roots of the component
                        AddRootsToDetectedComponent(component, dependencyGraph, componentRecorder);
                        component.DevelopmentDependency = MergeDevDependency(component.DevelopmentDependency, dependencyGraph.IsDevelopmentDependency(component.Component.Id));
                        component.DetectedBy = detector;

                        // Return in a format that allows us to add the additional files for the components
                        var locations = dependencyGraph.GetAdditionalRelatedFiles();
                        locations.Add(location);

                        var relativePaths = MakeFilePathsRelative(Logger, rootDirectory, locations);

                        foreach (var additionalRelatedFile in relativePaths ?? Enumerable.Empty<string>())
                        {
                            component.AddComponentFilePath(additionalRelatedFile);
                        }
                    }
                }

                return detectedComponents;
            }).ToList();
        }

        private List<DetectedComponent> FlattenAndMergeComponents(IEnumerable<DetectedComponent> components)
        {
            List<DetectedComponent> flattenedAndMergedComponents = new List<DetectedComponent>();
            foreach (var grouping in components.GroupBy(x => x.Component.Id + x.DetectedBy.Id))
            {
                flattenedAndMergedComponents.Add(MergeComponents(grouping));
            }

            return flattenedAndMergedComponents;
        }

        private bool? MergeDevDependency(bool? left, bool? right)
        {
            if (left == null)
            {
                return right;
            }

            if (right != null)
            {
                return left.Value && right.Value;
            }

            return left;
        }

        private DetectedComponent MergeComponents(IEnumerable<DetectedComponent> enumerable)
        {
            if (enumerable.Count() == 1)
            {
                return enumerable.First();
            }

            // Multiple detected components for the same logical component id -- this happens when different files see the same component. This code should go away when we get all
            //  mutable data out of detected component -- we can just take any component.
            var firstComponent = enumerable.First();
            foreach (var nextComponent in enumerable.Skip(1))
            {
                foreach (var filePath in nextComponent.FilePaths ?? Enumerable.Empty<string>())
                {
                    firstComponent.AddComponentFilePath(filePath);
                }

                foreach (var root in nextComponent.DependencyRoots ?? Enumerable.Empty<TypedComponent>())
                {
                    firstComponent.DependencyRoots.Add(root);
                }

                firstComponent.DevelopmentDependency = MergeDevDependency(firstComponent.DevelopmentDependency, nextComponent.DevelopmentDependency);

                if (nextComponent.ContainerDetailIds.Count > 0)
                {
                    foreach (var containerDetailId in nextComponent.ContainerDetailIds)
                    {
                        firstComponent.ContainerDetailIds.Add(containerDetailId);
                    }
                }
            }

            return firstComponent;
        }

        private void AddRootsToDetectedComponent(DetectedComponent detectedComponent, IDependencyGraph dependencyGraph, IComponentRecorder componentRecorder)
        {
            detectedComponent.DependencyRoots = detectedComponent.DependencyRoots ?? new HashSet<TypedComponent>(new ComponentComparer());

            if (dependencyGraph == null)
            {
                return;
            }

            var roots = dependencyGraph.GetExplicitReferencedDependencyIds(detectedComponent.Component.Id);

            foreach (var rootId in roots)
            {
                detectedComponent.DependencyRoots.Add(componentRecorder.GetComponent(rootId));
            }
        }

        private HashSet<string> MakeFilePathsRelative(ILogger logger, DirectoryInfo rootDirectory, HashSet<string> filePaths)
        {
            if (rootDirectory == null)
            {
                return null;
            }

            // Make relative Uri needs a trailing separator to ensure that we turn "directory we are scanning" into "/"
            string rootDirectoryFullName = rootDirectory.FullName;
            if (!rootDirectory.FullName.EndsWith(Path.DirectorySeparatorChar.ToString()) && !rootDirectory.FullName.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                rootDirectoryFullName += Path.DirectorySeparatorChar;
            }

            var rootUri = new Uri(rootDirectoryFullName);
            var relativePathSet = new HashSet<string>();
            foreach (var path in filePaths)
            {
                try
                {
                    var relativePath = rootUri.MakeRelativeUri(new Uri(path)).ToString();
                    if (!relativePath.StartsWith("/"))
                    {
                        relativePath = "/" + relativePath;
                    }

                    relativePathSet.Add(relativePath);
                }
                catch (UriFormatException)
                {
                    logger.LogVerbose($"The path: {path} could not be resolved relative to the root {rootUri}");
                }
            }

            return relativePathSet;
        }

        private ScannedComponent ConvertToContract(DetectedComponent component)
        {
            return new ScannedComponent
            {
                DetectorId = component.DetectedBy.Id,
                IsDevelopmentDependency = component.DevelopmentDependency,
                LocationsFoundAt = component.FilePaths,
                Component = component.Component,
                TopLevelReferrers = component.DependencyRoots,
                ContainerDetailIds = component.ContainerDetailIds,
                ContainerLayerIds = component.ContainerLayerIds,
            };
        }
    }
}
