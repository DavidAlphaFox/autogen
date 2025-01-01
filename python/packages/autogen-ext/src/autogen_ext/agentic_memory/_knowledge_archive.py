import os
from dataclasses import dataclass
import pickle
from typing import Dict, Optional, Union, List
from ._memo_store import MemoStore


@dataclass
class Insight:
    id: str
    insight_str: str
    task_str: str
    topics: List[str]


class KnowledgeArchive:
    """
    Stores task-completion insights in a vector DB for later retrieval.
    """
    def __init__(
        self,
        verbosity: Optional[int] = 0,
        reset: Optional[bool] = False,
        memory_dir: str = "tmp/memory",
        run_subdir: str = "run1",
        page_log=None,
    ):
        """
        Args:
            - verbosity (Optional, int): 1 to print memory operations, 0 to omit them. 3+ to print memo lists.
            - reset (Optional, bool): True to clear the DB before starting. Default False
            - memory_dir (Optional, str): path to the directory where all memory data is stored.
            - run_subdir (Optional, str): name of the subdirectory for this run's memory data.
            - page_log (Optional, PageLog): the PageLog object to use for logging.
        """
        memory_dir = os.path.expanduser(memory_dir)
        path_to_db_dir = os.path.join(memory_dir, run_subdir, "memo_store")
        self.path_to_dict = os.path.join(memory_dir, run_subdir, "uid_insight_dict.pkl")

        self.page_log = page_log
        parent_page = self.page_log.last_page()
        parent_page.add_lines("Creating KnowedgeArchive object", flush=True)

        self.memo_store = MemoStore(verbosity=verbosity, reset=reset, path_to_db_dir=path_to_db_dir)

        # Load or create the associated memo dict on disk.
        self.uid_insight_dict = {}
        self.last_insight_id = 0
        if (not reset) and os.path.exists(self.path_to_dict):
            parent_page.add_lines("\nLOADING INSIGHTS FROM DISK  {}".format(self.path_to_dict))
            parent_page.add_lines("    Location = {}".format(self.path_to_dict))
            with open(self.path_to_dict, "rb") as f:
                self.uid_insight_dict = pickle.load(f)
                self.last_insight_id = len(self.uid_insight_dict)
                parent_page.add_lines("\n{} INSIGHTS LOADED".format(len(self.uid_insight_dict)))

    def save_archive(self):
        self.memo_store.save_memos()
        parent_page = self.page_log.last_page()
        parent_page.add_lines("\nSAVING INSIGHTS TO DISK  {}".format(self.path_to_dict))
        with open(self.path_to_dict, "wb") as file:
            pickle.dump(self.uid_insight_dict, file)

    def add_insight(self, insight_str: str, task_str: Optional[str] = None, topics: Optional[List[str]] = None):
        """Adds an insight to the knowledge archive."""
        assert topics is not None, "For now, the topics list must be provided."
        self.last_insight_id += 1
        id_str = str(self.last_insight_id)
        insight = Insight(id=id_str, insight_str=insight_str, task_str=task_str, topics=topics)
        for topic in topics:
            # Add a mapping in the vec DB from each topic to the insight.
            self.memo_store.add_input_output_pair(topic, id_str)
        self.uid_insight_dict[str(id_str)] = insight
        self.save_archive()

    def get_relevant_insights(self, task_str: Optional[str] = None, topics: Optional[List[str]] = None):
        """Returns any insights from the knowledge archive that are relevant to the given task or topics."""
        assert (task_str is not None) or (topics is not None), "Either the task string or the topics list must be provided."
        assert topics is not None, "For now, the topics list is always required, because it won't be generated."

        # Build a dict of insight-relevance pairs.
        insight_relevance_dict = {}
        relevance_conversion_threshold = 1.7  # The approximate borderline between relevant and irrelevant topic matches.

        # Process the matching topics.
        matches = []  # Each match is a tuple: (topic, insight, distance)
        for topic in topics:
            matches.extend(self.memo_store.get_related_memos(topic, 25, 100))
        for match in matches:
            relevance = relevance_conversion_threshold - match[2]
            insight_id = match[1]
            insight_str = self.uid_insight_dict[insight_id].insight_str
            if insight_str in insight_relevance_dict:
                insight_relevance_dict[insight_str] += relevance
            else:
                insight_relevance_dict[insight_str] = relevance

        # Filter out insights with overall relevance below zero.
        for insight in list(insight_relevance_dict.keys()):
            if insight_relevance_dict[insight] < 0:
                del insight_relevance_dict[insight]

        return insight_relevance_dict

    def add_demonstration(self, task: str, demonstration: str, topics: List[str]):
        """Adds a task-demonstration pair (as a single insight) to the knowledge archive."""
        self.last_insight_id += 1
        id_str = str(self.last_insight_id)
        insight_str = "Example task:\n\n{}\nExample solution:\n\n{}".format(task, demonstration)
        insight = Insight(id=id_str, insight_str=insight_str, task_str=task, topics=topics)
        for topic in topics:
            # Add a mapping in the vec DB from each topic to the insight.
            self.memo_store.add_input_output_pair(topic, id_str)
        self.uid_insight_dict[str(id_str)] = insight
        self.save_archive()
