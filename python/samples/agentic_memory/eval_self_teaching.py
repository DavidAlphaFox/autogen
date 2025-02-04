import asyncio
import sys
from typing import Any, Dict

from autogen_core.models import (
    ChatCompletionClient,
)
from autogen_ext.agentic_memory import Apprentice, Grader, PageLogger

from utils import create_oai_client, load_yaml_file


async def eval_self_teaching(
    apprentice: Apprentice, client: ChatCompletionClient, logger: PageLogger, settings: Dict[str, Any]
) -> str:
    """
    Evaluates the ability of an agent to learn quickly from its own trial and error.
    """
    logger.enter_function()

    num_loops = settings["num_loops"]
    num_final_test_trials = settings["num_final_test_trials"]
    grader = Grader(client, logger)

    # Load the specified data.
    task_dict_1 = load_yaml_file(settings["task_file_1"])
    task_description_1 = task_dict_1["task_description"]
    expected_answer_1 = task_dict_1["expected_answer"]

    # Test generalization on this different, similar task.
    task_dict_2 = load_yaml_file(settings["task_file_2"])
    task_description_2 = task_dict_2["task_description"]
    expected_answer_2 = task_dict_2["expected_answer"]

    # Start the test with empty memory.
    apprentice.reset_memory()

    total_num_successes_1 = 0
    total_num_successes_2 = 0
    total_num_trials = 0
    for _ in range(num_loops):
        # Train on the first task.
        await apprentice.train_on_task(task=task_description_1, expected_answer=expected_answer_1)

        # Test on the first task.
        num_successes, num_trials = await grader.test_apprentice(
            apprentice=apprentice,
            task_description=task_description_1,
            expected_answer=expected_answer_1,
            num_trials=num_final_test_trials,
            use_memory=True,
            client=client,
            logger=logger,
        )
        logger.info("Task 1 success rate:  {}%".format(round((num_successes / num_trials) * 100)))
        total_num_successes_1 += num_successes

        # Test on the second task.
        num_successes, num_trials = await grader.test_apprentice(
            apprentice=apprentice,
            task_description=task_description_2,
            expected_answer=expected_answer_2,
            num_trials=num_final_test_trials,
            use_memory=True,
            client=client,
            logger=logger,
        )
        logger.info("Task 2 success rate:  {}%".format(round((num_successes / num_trials) * 100)))
        total_num_successes_2 += num_successes

        total_num_trials += num_final_test_trials
        logger.info("")

    overall_success_rate_1 = round((total_num_successes_1 / total_num_trials) * 100)
    overall_success_rate_2 = round((total_num_successes_2 / total_num_trials) * 100)

    results_str_1 = "Overall task 1 success rate:  {}%".format(overall_success_rate_1)
    results_str_2 = "Overall task 2 success rate:  {}%".format(overall_success_rate_2)
    logger.info("\n" + results_str_1)
    logger.info(results_str_2)

    logger.leave_function()
    return "\neval_self_teaching\n" + results_str_1 + "\n" + results_str_2


async def run_example(settings_filepath: str) -> None:
    """
    Runs the code example with the necessary components.
    """
    settings = load_yaml_file(settings_filepath)

    # Create the necessary components.
    logger = PageLogger(settings["PageLogger"])
    client = create_oai_client(settings["client"])
    apprentice = Apprentice(settings["Apprentice"], client, logger)

    # Call the example function.
    results = await eval_self_teaching(apprentice, client, logger, settings["test"])

    # Finish up.
    logger.flush(finished=True)
    print(results)


if __name__ == "__main__":
    args = sys.argv[1:]
    if len(args) != 1:
        # Print usage information.
        print("Usage:  amt.py <path to *.yaml file>")
    else:
        # Run the code example.
        asyncio.run(run_example(settings_filepath=args[0]))
