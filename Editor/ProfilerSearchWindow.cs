using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.Editor;
using Unity.Profiling.LowLevel;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UIElements;

public class ProfilerSearchUtil
{
    private const string c_searchContainerName = "profiler-search-extension";

    // inserts the search UI into the profiler window container..
    [MenuItem("Window/Profiler Search/Inject into Profiler Window")]
    public static void ShowWindow()
    {
        // no need to check for existing instance, open one if it doesn't exist.
        ProfilerWindow profilerWindow = EditorWindow.GetWindow<ProfilerWindow>();

        // check if profiler window already has the UI injected into it

        //profilerWindow.rootVisualElement.

        for (int i = 0; i < profilerWindow.rootVisualElement.childCount; ++i)
        {
            if (string.Equals(profilerWindow.rootVisualElement[i].name, c_searchContainerName, StringComparison.Ordinal))
            {
                // already inserted.
                return;
            }
        }

        if (s_instance == null)
        {
            s_instance = new ProfilerSearchUtil();
        }

        s_instance.CreateProfilerSearchGUI(profilerWindow.rootVisualElement);
    }

    private static ProfilerSearchUtil s_instance;

    private TextField m_searchText;
    private Button m_searchBackButton;
    private Button m_searchFwdButton;
    private Toggle m_caseSensitiveToggle;
    private Toggle m_matchFullWordToggle;
    private bool m_ignoreSelectionChange;

    public void CreateProfilerSearchGUI(VisualElement root)
    {
        if (root == null)
        {
            return;
        }

        {
            m_searchText = new TextField();
            m_searchBackButton = new Button();
            m_searchFwdButton = new Button();
            m_caseSensitiveToggle = new Toggle();
            m_matchFullWordToggle = new Toggle();

            m_searchText.name = c_searchContainerName;
            m_searchText.label = "Find";
            m_searchText.multiline = false;
            m_searchText.SetValueWithoutNotify(string.Empty);
            m_searchText.RegisterValueChangedCallback(evt =>
            {
                bool enable = !string.IsNullOrEmpty(evt.newValue);
                m_searchBackButton.SetEnabled(enable);
                m_searchFwdButton.SetEnabled(enable);
            });

            m_searchBackButton.name = "searchBack";
            m_searchBackButton.text = "<";
            m_searchBackButton.clicked += () => { SearchProfileCapture(m_searchText.value, -1, m_caseSensitiveToggle.value, m_matchFullWordToggle.value); };
            m_searchBackButton.SetEnabled(false);


            m_searchFwdButton.name = "searchFwd";
            m_searchFwdButton.text = ">";
            m_searchFwdButton.clicked += () => { SearchProfileCapture(m_searchText.value, 1, m_caseSensitiveToggle.value, m_matchFullWordToggle.value); };
            m_searchFwdButton.SetEnabled(false);

            m_caseSensitiveToggle.name = "ignoreCase";
            m_caseSensitiveToggle.text = "Case sensitive";

            m_matchFullWordToggle.name = "matchFull";
            m_matchFullWordToggle.text = "Match entire string";

            m_searchText.Add(m_searchBackButton);
            m_searchText.Add(m_searchFwdButton);
            m_searchText.Add(m_caseSensitiveToggle);
            m_searchText.Add(m_matchFullWordToggle);
            root.Add(m_searchText);
        }

        // register for profiler selection changed callbacks.
        if (EditorWindow.HasOpenInstances<ProfilerWindow>())
        {
            ProfilerWindow profilerWindow = EditorWindow.GetWindow<ProfilerWindow>();

            var sampleSelectionController = profilerWindow.GetFrameTimeViewSampleSelectionController(ProfilerWindow.cpuModuleIdentifier);
            sampleSelectionController.selectionChanged += OnProfilerSelectionChanged;
        }
    }

    void OnProfilerSelectionChanged(IProfilerFrameTimeViewSampleSelectionController controller, ProfilerTimeSampleSelection selection)
    {
        if (!m_ignoreSelectionChange)
        {
            if (selection != null)
            {

                m_searchText.SetValueWithoutNotify(selection != null
                    ? selection.markerNamePath[selection.markerNamePath.Count - 1]
                    : "");

                bool enable = selection != null;
                m_searchBackButton.SetEnabled(enable);
                m_searchFwdButton.SetEnabled(enable);


                /*
                 // potential optimization -
                 // marker ids don't change when multiple markers have the same name. so comparison against int is possible.
                 // but implies we can only match on full names.
                 // nb - ids DO change between (captured) frames. (check if different between threads as well? default markers don't change, e.g. UIR.DrawChain)
                 // however in cases where the marker doesn't exist in the framedata, we can skip searching it
                 // on the other hand, worst case is the marker doesn't exist anywhere in a capture, or at the end and beginning of a capture only.
                 // worst case is ~2.7s. in general things being searched for will either never appear (one time slowdown cost) or appear regularly enough for this not to be an issue.


                var frame = ProfilerDriver.GetRawFrameDataView((int)selection.frameIndex, controller.focusedThreadIndex);
                Debug.Log(frame.GetMarkerId(m_searchText.text));
                frame.Dispose();
                */

                m_matchFullWordToggle.value = !string.IsNullOrEmpty(m_searchText.text);
            }
        }

        m_ignoreSelectionChange = false;
    }

    void SearchProfileCapture(string text, int direction, bool caseSensitive, bool matchFullString)
    {
        Profiler.BeginSample("Search");

        StringBuilder sb = new StringBuilder(256);

        StringComparison caseMatch = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (EditorWindow.HasOpenInstances<ProfilerWindow>())
        {
            ProfilerWindow profilerWindow = EditorWindow.GetWindow<ProfilerWindow>();
            if (profilerWindow.selectedModuleIdentifier == ProfilerWindow.cpuModuleIdentifier)
            {
                long startFrameIndex = profilerWindow.selectedFrameIndex;

                var sampleSelectionController = profilerWindow.GetFrameTimeViewSampleSelectionController(ProfilerWindow.cpuModuleIdentifier);
                var threadIndex = sampleSelectionController.focusedThreadIndex >= 0 ? sampleSelectionController.focusedThreadIndex : 0;
                var selection = sampleSelectionController.selection;

                ulong currentSampleTime = direction < 0 ? ulong.MaxValue : 0;

                // store current selection's sample time if searching from an existing selection index
                if (selection != null)
                {
                    var frameData = ProfilerDriver.GetRawFrameDataView((int)startFrameIndex, threadIndex);
                    if (frameData.valid)
                    {
                        // store current search time
                        currentSampleTime = frameData.GetSampleStartTimeNs(selection.rawSampleIndex);
                    }
                    frameData.Dispose();
                }

                // search each thread
                ulong nearestSampleStartTime = direction < 0 ? 0 : ulong.MaxValue;

                ProfilerTimeSampleSelection nearestMatch = null;

                bool foundAnySample = false;

                int frame = (int)startFrameIndex;
                while (!foundAnySample)
                {
                    Profiler.BeginSample("Search Frame");
                    for (threadIndex = 0;; ++threadIndex)
                    {
                        Profiler.BeginSample("Search Thread");
                        var frameData = ProfilerDriver.GetRawFrameDataView(frame, threadIndex);
                        if (frameData.valid)
                        {
                            bool foundNextSample = false;
                            // search each frame starting at current

                            // if we're searching the currently-selected thread, we can shortcut some of the search by starting from the current index
                            // if it's not, we have to search from the first or last sample depending on direction.
                            int sampleIdx = threadIndex == sampleSelectionController.focusedThreadIndex &&
                                            frame == startFrameIndex && selection != null
                                ? selection.rawSampleIndex
                                : (direction < 0 ? frameData.sampleCount : -1);

                            while (!foundNextSample)
                            {
                                sampleIdx += direction;

                                // no more samples? advance to next thread (advance to next frame only once we've checked all threads in this frame)
                                if (sampleIdx < 0 || sampleIdx >= frameData.sampleCount)
                                {
                                    break;
                                }

                                Profiler.BeginSample("SampleName");
                                // can we use frameData.GetSampleMarkerId() instead?
                                // kind of - frameData.GetMarkerId() will return the marker id for a given string, but
                                // we can only use that for match entire string cases. searching for partial matches still
                                // requires a full search.

                                string sampleName = frameData.GetSampleName(sampleIdx);

                                Profiler.EndSample();
                                Profiler.BeginSample("Sample");

                                if (!string.IsNullOrEmpty(sampleName) &&
                                    (matchFullString ? sampleName.Equals(text, caseMatch) : sampleName.Contains(text, caseMatch)))
                                {
                                    ulong sampleTime = frameData.GetSampleStartTimeNs(sampleIdx);

                                    //if (sampleTime < nearestSampleStartTime)
                                    if ((direction > 0 && sampleTime > currentSampleTime &&
                                         sampleTime < nearestSampleStartTime) ||
                                        (direction < 0 && sampleTime < currentSampleTime &&
                                         sampleTime > nearestSampleStartTime))
                                    {
                                        Profiler.BeginSample("Match");
                                        nearestSampleStartTime = sampleTime;

                                        nearestMatch = new ProfilerTimeSampleSelection(frame,
                                            frameData.threadGroupName,
                                            frameData.threadName,
                                            frameData.threadId, sampleIdx, frameData.GetSampleName(sampleIdx));

                                        foundNextSample = true;
                                        foundAnySample = true;
                                        Profiler.EndSample();
                                    }
                                }

                                Profiler.EndSample();
                            }

                            frameData.Dispose();
                        }
                        else
                        {
                            // invalid frame data - last thread in current frame.
                            if (foundAnySample)
                            {
                                if (nearestSampleStartTime < ulong.MaxValue)
                                {
                                    m_ignoreSelectionChange = true;
                                    sampleSelectionController.SetSelection(nearestMatch);
                                }
                            }

                            // stop looking at this frame's threads, advance to next frame
                            Profiler.EndSample();
                            break;
                        }
                        Profiler.EndSample();
                    }

                    if (!foundAnySample)
                    {
                        frame += direction;
                        if (frame < 0 || frame >= profilerWindow.lastAvailableFrameIndex)
                        {
                            // no more frames to search - end.
                            break;
                        }
                    }
                    Profiler.EndSample();
                }
            }
        }
        Profiler.EndSample();
    }
}

public class ProfilerSearchWindow : EditorWindow
{
    [MenuItem("Window/Profiler Search/Standalone Window")]
    public static void ShowWindow()
    {
        /*
        if (!EditorWindow.HasOpenInstances<ProfilerWindow>())
        {
            Debug.Log("Please open a profiler window first");
            return;
        }
        */

        // ensure the profiler window is open before opening this (otherwise callbacks are not registered properly)
        ProfilerWindow profilerWindow = GetWindow<ProfilerWindow>();

        ProfilerSearchWindow window = GetWindow<ProfilerSearchWindow>();
        window.titleContent = new GUIContent("Profiler Search");
    }

    private ProfilerSearchUtil m_inspector;

    public void CreateGUI()
    {
        VisualElement root = rootVisualElement;

        // Profile Marker Search
        m_inspector = new ProfilerSearchUtil();
        m_inspector.CreateProfilerSearchGUI(root);
    }
}
